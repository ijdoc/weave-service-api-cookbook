// Recipe 04: update a Call's display_name after it finishes.
//
// Demonstrates the only mutation the service API exposes on a finished
// Call:
//   POST /call/update  -> change display_name
//
// Two wire-level quirks worth noting:
//
//   - The body is flat: top-level project_id, call_id, display_name.
//     /call/start and /call/end wrap their bodies under start / end;
//     /call/update does not. Sending {"update": {...}} will 422.
//   - The id field is named call_id, not id (which is what /call/end uses).
//
// display_name is the only user-modifiable field; attributes, inputs,
// output, etc. are immutable after /call/start.
//
// Run:
//   go run golang/04_update_call.go

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

const opName = "recipe-04-update-call"

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
		"cookbook.recipe":      "04_update_call",
		"cookbook.environment": getenv("COOKBOOK_ENVIRONMENT", "dev"),
	}
	inputs := map[string]any{"question": "What is the capital of Italy?"}
	output := map[string]any{"answer": "Rome"}
	newDisplayName := "recipe 04 — updated after finish"

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
	traceID := started["trace_id"].(string)
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

	// Mutate display_name. Flat body, `call_id` (not `id`), no wrapper key.
	postJSON("/call/update", map[string]any{
		"project_id":   projectID,
		"call_id":      callID,
		"display_name": newDisplayName,
	})
	fmt.Printf("Updated: id=%s display_name=%q\n", callID, newDisplayName)

	// --- verification ---
	// Read the Call back and assert display_name reflects the update.
	// Brief retry loop tolerates eventual consistency in the read path.
	var call map[string]any
	for i := 0; i < 5; i++ {
		res := postJSON("/call/read", map[string]any{"project_id": projectID, "id": callID})
		if c, ok := res["call"].(map[string]any); ok && c["display_name"] == newDisplayName {
			call = c
			break
		}
		time.Sleep(time.Second)
	}
	if call == nil || call["display_name"] != newDisplayName {
		fatal("FAIL: Call %s display_name not updated after 5 reads", callID)
	}

	// op_name and the rest must NOT have changed — /call/update only touches display_name.
	if call["op_name"] != opName {
		fatal("op_name drifted: %v", call["op_name"])
	}
	gotAttrs, _ := call["attributes"].(map[string]any)
	for k, v := range attributes {
		if gotAttrs[k] != v {
			fatal("attribute %s mismatch: %v", k, gotAttrs[k])
		}
	}
	if !reflect.DeepEqual(call["inputs"], inputs) {
		fatal("inputs mismatch: %v", call["inputs"])
	}
	if !reflect.DeepEqual(call["output"], output) {
		fatal("output mismatch: %v", call["output"])
	}
	if call["trace_id"] != traceID {
		fatal("trace_id mismatch: %v", call["trace_id"])
	}
	fmt.Printf("Verified: id=%s display_name=%q\n", callID, call["display_name"])
}
