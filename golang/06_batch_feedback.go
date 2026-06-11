// Recipe 06: attach feedback to many Calls in one request.
//
// Demonstrates the bulk variant of /feedback/create:
//   POST /feedback/batch/create  -> N feedback items in one round trip
//
// Two wire-level points worth knowing:
//
//   - The path is /feedback/batch/create, not the more guessable
//     /feedback/create-batch or /feedback/createBatch.
//   - The body wraps a parallel-indexed array under `batch`:
//       {"batch": [<FeedbackCreateReq>, <FeedbackCreateReq>, ...]}
//     Each item carries its own project_id, weave_ref, feedback_type,
//     and payload — exactly the shape /feedback/create takes. The
//     response mirrors the input with {"res": [<FeedbackCreateRes>, ...]},
//     indices aligned to the input batch.
//
// This recipe creates three Calls and attaches two feedback items per
// Call in a single batch request: a wandb.note.1 (UI-visible in the
// trace table) and a custom scorer-style feedback. One round trip ships
// 6 items; the per-item endpoint would need 6. Mirrors recipe 05's
// note + scorer split, but bulk.
//
// Run:
//   go run golang/06_batch_feedback.go

package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"reflect"
	"strings"
	"time"
)

const (
	noteType   = "wandb.note.1"
	scorerType = "recipe-06-scorer-correctness"
)

var (
	baseURL   = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai")
	projectID = os.Getenv("WANDB_ENTITY") + "/" + os.Getenv("WANDB_PROJECT")
	apiKey    = os.Getenv("WANDB_API_KEY")
	client    = &http.Client{}

	baseAttributes = map[string]any{
		"cookbook.language":    "golang",
		"cookbook.recipe":      "06_batch_feedback",
		"cookbook.environment": getenv("COOKBOOK_ENVIRONMENT", "dev"),
	}
)

func getenv(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, format+"\n", args...)
	os.Exit(1)
}

// postJSON centralizes auth + JSON (de)serialization. Any non-2xx is fatal.
func postJSON(path string, body map[string]any) map[string]any {
	payload, err := json.Marshal(body)
	if err != nil {
		fatal("encode %s: %v", path, err)
	}
	req, err := http.NewRequest(http.MethodPost, baseURL+path, bytes.NewReader(payload))
	if err != nil {
		fatal("request %s: %v", path, err)
	}
	req.SetBasicAuth("api", apiKey)
	req.Header.Set("Content-Type", "application/json")
	res, err := client.Do(req)
	if err != nil {
		fatal("post %s: %v", path, err)
	}
	defer res.Body.Close()
	raw, _ := io.ReadAll(res.Body)
	if res.StatusCode/100 != 2 {
		fatal("HTTP %d for %s: %s", res.StatusCode, path, raw)
	}
	if len(raw) == 0 {
		return map[string]any{}
	}
	var out map[string]any
	if err := json.Unmarshal(raw, &out); err != nil {
		fatal("decode %s: %v", path, err)
	}
	return out
}

func startCall(opName string, inputs map[string]any) string {
	started := postJSON("/call/start", map[string]any{
		"start": map[string]any{
			"project_id": projectID,
			"op_name":    opName,
			"started_at": time.Now().UTC().Format(time.RFC3339Nano),
			"attributes": baseAttributes,
			"inputs":     inputs,
		},
	})
	return started["id"].(string)
}

func endCall(callID string, output map[string]any) {
	postJSON("/call/end", map[string]any{
		"end": map[string]any{
			"project_id": projectID,
			"id":         callID,
			"ended_at":   time.Now().UTC().Format(time.RFC3339Nano),
			"summary":    map[string]any{},
			"output":     output,
		},
	})
}

func main() {
	var missing []string
	for _, k := range []string{"WANDB_API_KEY", "WANDB_ENTITY", "WANDB_PROJECT"} {
		if os.Getenv(k) == "" {
			missing = append(missing, k)
		}
	}
	if len(missing) > 0 {
		fatal("Missing required env vars: %s. See ../README.md#setup.", strings.Join(missing, ", "))
	}

	// Create three Calls — same shape as recipe 01, just repeated.
	questions := []struct{ Question, Answer string }{
		{"What is the capital of France?", "Paris"},
		{"What is the capital of Spain?", "Madrid"},
		{"What is the capital of Italy?", "Rome"},
	}
	type call struct{ id, ref, answer string }
	var calls []call
	for i, q := range questions {
		callID := startCall(fmt.Sprintf("recipe-06-call-%d", i+1), map[string]any{"question": q.Question})
		endCall(callID, map[string]any{"answer": q.Answer})
		calls = append(calls, call{callID, fmt.Sprintf("weave:///%s/call/%s", projectID, callID), q.Answer})
		fmt.Printf("Call %d: id=%s\n", i+1, callID)
	}

	// Build the batch — note + scorer feedback per Call (6 items total).
	var batch []map[string]any
	for _, c := range calls {
		batch = append(batch,
			map[string]any{
				"project_id":    projectID,
				"weave_ref":     c.ref,
				"feedback_type": noteType,
				"payload":       map[string]any{"note": fmt.Sprintf("Reviewed — answer: '%s'", c.answer)},
			},
			map[string]any{
				"project_id":    projectID,
				"weave_ref":     c.ref,
				"feedback_type": scorerType,
				"payload":       map[string]any{"output": map[string]any{"score": 1.0, "reason": fmt.Sprintf("Answer '%s' matches expected", c.answer)}},
			},
		)
	}

	// Single round trip for all six items.
	resp := postJSON("/feedback/batch/create", map[string]any{"batch": batch})
	results, _ := resp["res"].([]any)
	if len(results) != len(batch) {
		fatal("batch size mismatch: sent %d got %d", len(batch), len(results))
	}
	for i, item := range batch {
		res, _ := results[i].(map[string]any)
		fmt.Printf("Batch->Feedback: type=%s feedback_id=%v\n", item["feedback_type"], res["id"])
	}

	// --- verification ---
	// For each Call, query feedback by weave_ref and assert both the note and
	// the scorer feedback landed with the expected payload. Brief retry
	// tolerates eventual consistency in the read path.
	expectedTypes := []string{noteType, scorerType}
	for _, c := range calls {
		expectedNote := map[string]any{"note": fmt.Sprintf("Reviewed — answer: '%s'", c.answer)}
		expectedScorer := map[string]any{"output": map[string]any{"score": 1.0, "reason": fmt.Sprintf("Answer '%s' matches expected", c.answer)}}
		byType := map[string]map[string]any{}
		for i := 0; i < 5; i++ {
			res := postJSON("/feedback/query", map[string]any{
				"project_id": projectID,
				"query": map[string]any{
					"$expr": map[string]any{
						"$eq": []any{
							map[string]any{"$getField": "weave_ref"},
							map[string]any{"$literal": c.ref},
						},
					},
				},
			})
			byType = map[string]map[string]any{}
			if result, ok := res["result"].([]any); ok {
				for _, row := range result {
					if r, ok := row.(map[string]any); ok {
						if t, ok := r["feedback_type"].(string); ok {
							byType[t] = r
						}
					}
				}
			}
			have := true
			for _, t := range expectedTypes {
				if _, ok := byType[t]; !ok {
					have = false
					break
				}
			}
			if have {
				break
			}
			time.Sleep(time.Second)
		}
		for _, t := range expectedTypes {
			if _, ok := byType[t]; !ok {
				fatal("FAIL: feedback for %s not all visible after 5 reads", c.ref)
			}
		}
		if !reflect.DeepEqual(byType[noteType]["payload"], expectedNote) {
			fatal("note payload for %s: %v", c.id, byType[noteType]["payload"])
		}
		if !reflect.DeepEqual(byType[scorerType]["payload"], expectedScorer) {
			fatal("scorer payload for %s: %v", c.id, byType[scorerType]["payload"])
		}
		for _, row := range byType {
			if row["weave_ref"] != c.ref {
				fatal("weave_ref drift: %v", row["weave_ref"])
			}
		}
	}
	fmt.Printf("Verified: %d batched feedback items across %d Calls (note + scorer each)\n", len(batch), len(calls))
}
