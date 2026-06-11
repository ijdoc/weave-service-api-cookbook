// Recipe 05: attach feedback to a Call.
//
// Demonstrates the feedback lifecycle:
//   POST /feedback/create  -> attach feedback to a Call
//   POST /feedback/query   -> read it back
//
// Three wire-level points worth knowing:
//
//   - The Call is identified by weave_ref, not call_id directly:
//       weave:///{entity}/{project}/call/{call_id}
//     The recipe constructs this URI inline. A call_ref field also
//     exists, but weave_ref is the required one.
//   - /feedback/create body is flat — top-level project_id, weave_ref,
//     feedback_type, payload (no wrapper key, like /call/update).
//   - /feedback/query uses the typed Query language. Filtering by
//     weave_ref is {"$expr": {"$eq": [{"$getField": "weave_ref"},
//     {"$literal": "weave:///..."}]}}.
//
// feedback_type is a freeform string. By convention wandb.<kind>.<version>
// is reserved for W&B-recognized types with UI treatment (wandb.note.1,
// wandb.reaction.1); scorer-emitted feedback uses the scorer name as a
// prefix. This recipe attaches one of each to show the many-to-one shape.
//
// Run:
//   go run golang/05_add_feedback.go

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

const opName = "recipe-05-add-feedback"

var (
	baseURL   = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai")
	projectID = os.Getenv("WANDB_ENTITY") + "/" + os.Getenv("WANDB_PROJECT")
	apiKey    = os.Getenv("WANDB_API_KEY")
	client    = &http.Client{}
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

	attributes := map[string]any{
		"cookbook.language":    "golang",
		"cookbook.recipe":      "05_add_feedback",
		"cookbook.environment": getenv("COOKBOOK_ENVIRONMENT", "dev"),
	}
	inputs := map[string]any{"question": "What is the capital of Germany?"}
	output := map[string]any{"answer": "Berlin"}

	// Two feedback items, illustrating the type-convention split.
	humanType := "wandb.note.1"
	humanPayload := map[string]any{"note": "Answer looks correct."}
	scorerType := "recipe-05-scorer-correctness"
	scorerPayload := map[string]any{"output": map[string]any{"score": 1.0, "reason": "Answer matches expected"}}

	// Open the Call.
	started := postJSON("/call/start", map[string]any{
		"start": map[string]any{
			"project_id": projectID,
			"op_name":    opName,
			"started_at": time.Now().UTC().Format(time.RFC3339Nano),
			"attributes": attributes,
			"inputs":     inputs,
		},
	})
	callID := started["id"].(string)
	fmt.Printf("Started: id=%s\n", callID)

	// Close it.
	postJSON("/call/end", map[string]any{
		"end": map[string]any{
			"project_id": projectID,
			"id":         callID,
			"ended_at":   time.Now().UTC().Format(time.RFC3339Nano),
			"summary":    map[string]any{},
			"output":     output,
		},
	})
	fmt.Printf("Ended:   id=%s\n", callID)

	// Build the Call's weave_ref. /feedback/create takes this URI string,
	// not a raw call_id.
	callRef := fmt.Sprintf("weave:///%s/call/%s", projectID, callID)

	// Attach both feedback items.
	feedbacks := []struct {
		Type    string
		Payload map[string]any
	}{
		{humanType, humanPayload},
		{scorerType, scorerPayload},
	}
	for _, fb := range feedbacks {
		res := postJSON("/feedback/create", map[string]any{
			"project_id":    projectID,
			"weave_ref":     callRef,
			"feedback_type": fb.Type,
			"payload":       fb.Payload,
		})
		fmt.Printf("Feedback: id=%v type=%s\n", res["id"], fb.Type)
	}

	// --- verification ---
	// Query feedback filtered to this Call by weave_ref, asserting both items
	// land with the expected feedback_type + payload. Brief retry tolerates
	// eventual consistency in the read path.
	expectedTypes := []string{humanType, scorerType}
	byType := map[string]map[string]any{}
	for i := 0; i < 5; i++ {
		res := postJSON("/feedback/query", map[string]any{
			"project_id": projectID,
			"query": map[string]any{
				"$expr": map[string]any{
					"$eq": []any{
						map[string]any{"$getField": "weave_ref"},
						map[string]any{"$literal": callRef},
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
			fatal("FAIL: feedback for %s not all visible after 5 reads", callRef)
		}
	}
	if !reflect.DeepEqual(byType[humanType]["payload"], humanPayload) {
		fatal("human payload: %v", byType[humanType]["payload"])
	}
	if !reflect.DeepEqual(byType[scorerType]["payload"], scorerPayload) {
		fatal("scorer payload: %v", byType[scorerType]["payload"])
	}
	for _, row := range byType {
		if row["weave_ref"] != callRef {
			fatal("weave_ref drift: %v", row["weave_ref"])
		}
	}
	fmt.Printf("Verified: %d feedback items on %s\n", len(byType), callRef)
}
