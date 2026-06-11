// Recipe 02: query Calls via /calls/stream_query.
//
// Demonstrates the workhorse read endpoint:
//   POST /calls/stream_query  -> stream NDJSON of matching Calls
//
// Sets up by creating one Call (op_name="recipe-02-query-call"), then
// queries that op_name and confirms the just-created Call appears in
// the streamed results.
//
// The endpoint returns one JSON object per line (application/jsonl); we
// scan the body line-by-line rather than buffering the whole response.
//
// Run:
//   go run golang/02_query_call.go

package main

import (
	"bufio"
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

const opName = "recipe-02-query-call"

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

// postJSON centralizes auth + JSON (de)serialization; the per-call payload
// shape stays visible at the call sites below. Any non-2xx response is fatal.
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

// streamQuery POSTs to /calls/stream_query and parses the NDJSON response
// line-by-line, returning the matching Call rows. Each line is one JSON
// object, so we scan the body as it arrives rather than buffering it whole.
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
	scanner.Buffer(make([]byte, 0, 64*1024), 1024*1024) // a single Call row can exceed the 64KB default
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
		"cookbook.recipe":      "02_query_call",
		"cookbook.environment": getenv("COOKBOOK_ENVIRONMENT", "dev"),
	}
	inputs := map[string]any{"question": "What is the capital of Spain?"}
	output := map[string]any{"answer": "Madrid"}

	// Setup: create + end a Call we can later query for.
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
	fmt.Printf("Created: id=%s\n", callID)

	postJSON("/call/end", map[string]any{
		"end": map[string]any{
			"project_id": projectID,
			"id":         callID,
			"ended_at":   time.Now().UTC().Format(time.RFC3339Nano),
			"summary":    map[string]any{},
			"output":     output,
		},
	})

	// Query: stream Calls matching our op_name, newest first. Retry briefly
	// to tolerate eventual consistency on the read path.
	var found map[string]any
	for i := 0; i < 5; i++ {
		rows := streamQuery(map[string]any{
			"project_id": projectID,
			"filter":     map[string]any{"op_names": []string{opName}},
			"sort_by":    []map[string]any{{"field": "started_at", "direction": "desc"}},
			"limit":      50,
		})
		for _, c := range rows {
			// Require ended_at populated so we don't race the write-to-read
			// propagation and read a half-finalized row.
			if c["id"] == callID && c["ended_at"] != nil {
				found = c
				break
			}
		}
		if found != nil {
			break
		}
		time.Sleep(time.Second)
	}

	// --- verification ---
	if found == nil {
		fatal("FAIL: Call %s not in stream_query results after 5 attempts", callID)
	}

	if found["op_name"] != opName {
		fatal("op_name mismatch: %v", found["op_name"])
	}
	gotAttrs, _ := found["attributes"].(map[string]any)
	for k, v := range attributes {
		if gotAttrs[k] != v {
			fatal("attribute %s mismatch: %v", k, gotAttrs[k])
		}
	}
	if !reflect.DeepEqual(found["inputs"], inputs) {
		fatal("inputs mismatch: %v", found["inputs"])
	}
	if !reflect.DeepEqual(found["output"], output) {
		fatal("output mismatch: %v", found["output"])
	}
	if found["trace_id"] != traceID {
		fatal("trace_id mismatch: %v", found["trace_id"])
	}
	fmt.Printf("Verified: id=%s\n", callID)
}
