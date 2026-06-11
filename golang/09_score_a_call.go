// Recipe 09: create a Scorer Op + score a Call (the apply_scorer pattern).
//
// Wire-level equivalent of the SDK's result.call.apply_scorer(scorer) —
// score an already-logged Call without the full evaluation flow (recipes
// 11-13). Reuses the ADR-0004 Op-creation pattern from recipe 08, this
// time for a scorer function.
//
// A Scorer Op is just an Op whose role is to score a Call's output. There
// is no separate Scorer Object to register — POST /v2/.../scorers exists
// but the cookbook does not use it; the Op pattern is what @weave.op
// scorer functions use and what apply_scorer integrates with.
//
// This recipe builds three things on the wire:
//
//  1. A small model Call producing a sample prediction.
//  2. A scoring Call invoking the Scorer Op (prediction + expected as
//     inputs, the score value as output). Top-level standalone Call.
//  3. A wandb.runnable.<scorer_op_id> Feedback row on the prediction
//     Call — the load-bearing link that makes the score render inline
//     under the prediction in the W&B UI. The Feedback carries
//     feedback_type, payload={"output": <score>}, runnable_ref (Scorer
//     Op ref) and call_ref (score Call ref).
//
// Scorer Op object_ids are NOT aggregator-filtered, so per-language
// naming (recipe-09-is-correct-golang) is fine. The Scorer Op's source
// carries the ADR-0004 scaffold.
//
// Run:
//   go run golang/09_score_a_call.go

package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"reflect"
	"strings"
	"time"
)

const recipePath = "golang/09_score_a_call.go"

var (
	entity    = os.Getenv("WANDB_ENTITY")
	project   = os.Getenv("WANDB_PROJECT")
	projectID = entity + "/" + project
	baseURL   = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai")
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

// post centralizes auth + JSON (de)serialization. Any non-2xx is fatal.
func post(path string, body map[string]any) map[string]any {
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
		"cookbook.recipe":      "09_score_a_call",
		"cookbook.environment": getenv("COOKBOOK_ENVIRONMENT", "dev"),
	}

	// --- ADR-0004 scaffold for the Scorer Op ---
	recipeBytes, err := os.ReadFile(recipePath)
	if err != nil {
		fatal("read recipe source %s: %v", recipePath, err)
	}
	sum := sha256.Sum256(recipeBytes)
	recipeSha := hex.EncodeToString(sum[:])[:16]

	scorerSource := fmt.Sprintf(`# Cookbook scaffold (golang)
# Source: %s
# SHA256: %s

import weave


@weave.op
def is_correct(output, expected):
    """The actual scoring implementation lives in:
        %s

    Byte-for-byte reference (SHA256 of the recipe file):
        %s

    To verify a local copy of the file matches (POSIX shell):
        shasum -a 256 %s | cut -c1-16

    This Python op is a metadata handle, not the real scorer — running
    it raises NotImplementedError by design.
    """
    raise NotImplementedError(
        "This op is a Python scaffold uploaded from a non-Python recipe. "
        "See the docstring above for the real source-language file and a "
        "verifiable byte-for-byte reference (SHA256)."
    )
`, recipePath, recipeSha, recipePath, recipeSha, recipePath)

	// 1) Register the Scorer Op. Per-language object_id; the server lowercases
	// it. Scorer Op names are not aggregator-filtered, so this is fine.
	scorerOpID := "recipe-09-is-correct-golang"
	scorerRes := post(fmt.Sprintf("/v2/%s/%s/ops", entity, project), map[string]any{
		"name":        scorerOpID,
		"source_code": scorerSource,
	})
	scorerOpRef := fmt.Sprintf("weave:///%s/op/%s:%s", projectID, scorerRes["object_id"], scorerRes["digest"])
	fmt.Printf("Scorer op:  %v digest=%v… version=%v\n", scorerRes["object_id"], scorerRes["digest"].(string)[:12], scorerRes["version_index"])

	// 2) Produce a sample prediction via a tiny model Call (op_name is a plain
	// string here — no Model/predict Op, since this recipe's focus is scoring).
	question := "Is the sky blue?"
	expected := "yes"
	prediction := "yes"
	started := post("/call/start", map[string]any{
		"start": map[string]any{
			"project_id": projectID,
			"op_name":    "recipe-09-mock-predict",
			"started_at": time.Now().UTC().Format(time.RFC3339Nano),
			"attributes": attributes,
			"inputs":     map[string]any{"question": question},
		},
	})
	predictCallID := started["id"].(string)
	post("/call/end", map[string]any{
		"end": map[string]any{
			"project_id": projectID,
			"id":         predictCallID,
			"ended_at":   time.Now().UTC().Format(time.RFC3339Nano),
			"summary": map[string]any{
				"status_counts": map[string]any{"success": 1, "error": 0},
				"weave":         map[string]any{"status": "success", "trace_name": "recipe-09-mock-predict"},
			},
			// Per the cookbook's question/answer convention, predict outputs
			// land under an `answer` key; the Scorer Op below takes the raw
			// answer value as its `output` argument.
			"output": map[string]any{"answer": prediction},
		},
	})
	fmt.Printf("Predicted:  id=%s output=%q\n", predictCallID, prediction)

	// 3) Open a scoring Call invoking the Scorer Op. op_name MUST be the Op's
	// weave:// ref. Inputs are what's being scored; output is the score value
	// (boolean — recipe 13's aggregation classifies this as a binary type).
	startedScore := post("/call/start", map[string]any{
		"start": map[string]any{
			"project_id": projectID,
			"op_name":    scorerOpRef,
			"started_at": time.Now().UTC().Format(time.RFC3339Nano),
			"attributes": attributes,
			"inputs":     map[string]any{"output": prediction, "expected": expected},
		},
	})
	scoreCallID := startedScore["id"].(string)
	score := prediction == expected
	post("/call/end", map[string]any{
		"end": map[string]any{
			"project_id": projectID,
			"id":         scoreCallID,
			"ended_at":   time.Now().UTC().Format(time.RFC3339Nano),
			"summary": map[string]any{
				"status_counts": map[string]any{"success": 1, "error": 0},
				"weave":         map[string]any{"status": "success", "trace_name": scorerOpID},
			},
			"output": score,
		},
	})
	fmt.Printf("Scored:     id=%s output=%v\n", scoreCallID, score)

	// 4) Link the score to the prediction Call via a wandb.runnable.<id>
	// Feedback row on the prediction. The UI uses this Feedback (not any
	// parent-child structure) to render the score inline on the prediction.
	predictCallRef := fmt.Sprintf("weave:///%s/call/%s", projectID, predictCallID)
	scoreCallRef := fmt.Sprintf("weave:///%s/call/%s", projectID, scoreCallID)
	feedbackType := "wandb.runnable." + scorerOpID
	feedbackRes := post("/feedback/create", map[string]any{
		"project_id":    projectID,
		"weave_ref":     predictCallRef,
		"feedback_type": feedbackType,
		"payload":       map[string]any{"output": score},
		"runnable_ref":  scorerOpRef,
		"call_ref":      scoreCallRef,
	})
	fmt.Printf("Linked:     feedback id=%v on predict call (feedback_type=%s)\n", feedbackRes["id"], feedbackType)

	// --- verification ---
	// (a) The scoring Call round-trips with the right op_ref + inputs + output.
	var call map[string]any
	for i := 0; i < 5; i++ {
		res := post("/call/read", map[string]any{"project_id": projectID, "id": scoreCallID})
		if c, ok := res["call"].(map[string]any); ok && c["ended_at"] != nil {
			call = c
			break
		}
		time.Sleep(time.Second)
	}
	if call == nil || call["ended_at"] == nil {
		fatal("FAIL: scoring Call %s not visible/finished after 5 reads", scoreCallID)
	}
	if call["op_name"] != scorerOpRef {
		fatal("op_name: %v", call["op_name"])
	}
	ins, _ := call["inputs"].(map[string]any)
	if ins["output"] != prediction {
		fatal("inputs.output: %v", ins["output"])
	}
	if ins["expected"] != expected {
		fatal("inputs.expected: %v", ins["expected"])
	}
	if call["output"] != score {
		fatal("output: %v", call["output"])
	}

	// (b) The wandb.runnable.* Feedback row exists on the prediction Call.
	var linking map[string]any
	for i := 0; i < 5; i++ {
		res := post("/feedback/query", map[string]any{
			"project_id": projectID,
			"query": map[string]any{
				"$expr": map[string]any{
					"$eq": []any{
						map[string]any{"$getField": "weave_ref"},
						map[string]any{"$literal": predictCallRef},
					},
				},
			},
		})
		linking = nil
		if result, ok := res["result"].([]any); ok {
			for _, row := range result {
				if r, ok := row.(map[string]any); ok && r["feedback_type"] == feedbackType {
					linking = r
					break
				}
			}
		}
		if linking != nil {
			break
		}
		time.Sleep(time.Second)
	}
	if linking == nil {
		fatal("FAIL: no %q feedback on %s after 5 reads", feedbackType, predictCallRef)
	}
	if !reflect.DeepEqual(linking["payload"], map[string]any{"output": score}) {
		fatal("payload: %v", linking["payload"])
	}
	if linking["runnable_ref"] != scorerOpRef {
		fatal("runnable_ref: %v", linking["runnable_ref"])
	}
	if linking["call_ref"] != scoreCallRef {
		fatal("call_ref: %v", linking["call_ref"])
	}
	fmt.Printf("Verified:   id=%s (scorer op + inputs + score output round-tripped)\n", scoreCallID)
	fmt.Printf("Verified:   %s feedback links score -> predict\n", feedbackType)
}
