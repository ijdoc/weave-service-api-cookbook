// Recipe 03: parent + child Calls (RAG-shaped trace).
//
// Demonstrates Trace structure: one parent Call with two child Calls
// underneath. Children declare their parent via `parent_id` on
// /call/start and share the parent's `trace_id` explicitly.
//
// The RAG-shaped flow:
//   rag_pipeline (parent)
//   |-- retrieve  (child 1)
//   `-- generate  (child 2)
//
// Ordering matters: a child's /call/start happens after the parent's
// /call/start, and each child's /call/end happens before the parent's
// /call/end. The recipe shows this canonical order.
//
// Verification queries /calls/stream_query by trace_id, gets all three
// Calls back, and asserts the parent/child structure is what we wrote.
//
// Run:
//   go run golang/03_parent_child_calls.go

package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"
)

var (
	baseURL   = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai")
	projectID = os.Getenv("WANDB_ENTITY") + "/" + os.Getenv("WANDB_PROJECT")
	apiKey    = os.Getenv("WANDB_API_KEY")
	client    = &http.Client{}

	baseAttributes = map[string]any{
		"cookbook.language":    "golang",
		"cookbook.recipe":      "03_parent_child_calls",
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

// streamQuery parses the NDJSON /calls/stream_query response line-by-line.
func streamQuery(body map[string]any) []map[string]any {
	payload, err := json.Marshal(body)
	if err != nil {
		fatal("encode /calls/stream_query: %v", err)
	}
	req, err := http.NewRequest(http.MethodPost, baseURL+"/calls/stream_query", bytes.NewReader(payload))
	if err != nil {
		fatal("request /calls/stream_query: %v", err)
	}
	req.SetBasicAuth("api", apiKey)
	req.Header.Set("Content-Type", "application/json")
	res, err := client.Do(req)
	if err != nil {
		fatal("post /calls/stream_query: %v", err)
	}
	defer res.Body.Close()
	if res.StatusCode/100 != 2 {
		raw, _ := io.ReadAll(res.Body)
		fatal("HTTP %d for /calls/stream_query: %s", res.StatusCode, raw)
	}
	var rows []map[string]any
	scanner := bufio.NewScanner(res.Body)
	scanner.Buffer(make([]byte, 0, 64*1024), 1024*1024)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		var row map[string]any
		if err := json.Unmarshal([]byte(line), &row); err != nil {
			fatal("decode stream row: %v", err)
		}
		rows = append(rows, row)
	}
	if err := scanner.Err(); err != nil {
		fatal("read /calls/stream_query: %v", err)
	}
	return rows
}

// startCall POSTs /call/start. parentID and traceID are omitted when empty,
// so a top-level Call passes "" for both and the server assigns a trace_id.
func startCall(opName string, inputs map[string]any, parentID, traceID string) map[string]any {
	start := map[string]any{
		"project_id": projectID,
		"op_name":    opName,
		"started_at": time.Now().UTC().Format(time.RFC3339Nano),
		"attributes": baseAttributes,
		"inputs":     inputs,
	}
	if parentID != "" {
		start["parent_id"] = parentID
	}
	if traceID != "" {
		start["trace_id"] = traceID
	}
	return postJSON("/call/start", map[string]any{"start": start})
}

// endCall POSTs /call/end.
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

	question := "Where is the Eiffel Tower?"
	docs := []string{"Paris", "France"}
	answer := "In Paris, France."

	// Open the parent (top-level: no parent_id, no explicit trace_id).
	// The server assigns a trace_id which we propagate to children.
	parent := startCall("recipe-03-rag-pipeline", map[string]any{"question": question}, "", "")
	parentID := parent["id"].(string)
	traceID := parent["trace_id"].(string)
	fmt.Printf("Started parent: id=%s trace_id=%s\n", parentID, traceID)

	// Open + finish the first child (retrieve), under the parent + trace.
	retrieve := startCall("recipe-03-retrieve", map[string]any{"question": question}, parentID, traceID)
	retrieveID := retrieve["id"].(string)
	fmt.Printf("Started child 1: id=%s\n", retrieveID)
	endCall(retrieveID, map[string]any{"docs": docs})
	fmt.Printf("Ended   child 1: id=%s\n", retrieveID)

	// Open + finish the second child (generate).
	generate := startCall("recipe-03-generate", map[string]any{"docs": docs, "question": question}, parentID, traceID)
	generateID := generate["id"].(string)
	fmt.Printf("Started child 2: id=%s\n", generateID)
	endCall(generateID, map[string]any{"answer": answer})
	fmt.Printf("Ended   child 2: id=%s\n", generateID)

	// Close the parent (after all children have finished).
	endCall(parentID, map[string]any{"answer": answer})
	fmt.Printf("Ended   parent:  id=%s\n", parentID)

	// --- verification ---
	// Stream all Calls in this trace; assert parent + 2 children, with
	// parent.parent_id absent and children.parent_id = parent_id.
	expected := []string{parentID, retrieveID, generateID}
	found := map[string]map[string]any{}
	for i := 0; i < 5; i++ {
		rows := streamQuery(map[string]any{
			"project_id": projectID,
			"filter":     map[string]any{"trace_ids": []string{traceID}},
		})
		found = map[string]map[string]any{}
		for _, c := range rows {
			if id, ok := c["id"].(string); ok {
				found[id] = c
			}
		}
		// Require all three visible AND finalized (ended_at populated) so we
		// don't race write-to-read propagation on inner-field reads.
		ready := true
		for _, id := range expected {
			if c, ok := found[id]; !ok || c["ended_at"] == nil {
				ready = false
				break
			}
		}
		if ready {
			break
		}
		time.Sleep(time.Second)
	}

	for _, id := range expected {
		if _, ok := found[id]; !ok {
			fatal("FAIL: trace %s missing call %s", traceID, id)
		}
	}

	parentCall, retrieveCall, generateCall := found[parentID], found[retrieveID], found[generateID]
	if parentCall["parent_id"] != nil {
		fatal("parent has parent_id: %v", parentCall["parent_id"])
	}
	if retrieveCall["parent_id"] != parentID {
		fatal("retrieve.parent_id: %v", retrieveCall["parent_id"])
	}
	if generateCall["parent_id"] != parentID {
		fatal("generate.parent_id: %v", generateCall["parent_id"])
	}
	for _, c := range []map[string]any{parentCall, retrieveCall, generateCall} {
		if c["trace_id"] != traceID {
			fatal("trace_id on %v: %v", c["id"], c["trace_id"])
		}
		attrs, _ := c["attributes"].(map[string]any)
		for k, v := range baseAttributes {
			if attrs[k] != v {
				fatal("attribute %s on %v: %v", k, c["id"], attrs[k])
			}
		}
	}
	fmt.Printf("Verified: trace_id=%s (1 parent + 2 children)\n", traceID)
}
