// Recipe 10: create a Dataset and read its rows back.
//
// Demonstrates the v2 Dataset endpoints plus the Table read needed to
// walk the rows:
//   POST /v2/{entity}/{project}/datasets
//       -> create the Dataset, returns (object_id, digest, version_index)
//   GET  /v2/{entity}/{project}/datasets/{object_id}/versions/{digest}
//       -> read Dataset metadata, including a *reference* to its rows
//   POST /table/query
//       -> read the actual rows out of the referenced Table
//
// Wire-level points worth knowing:
//
//   - These are v2 endpoints under /v2/{entity}/{project}/datasets, not a
//     v1-style /datasets/create. Entity + project live in the URL path.
//     Read uses GET (the rest of the API is POST-only); create uses POST.
//   - A Dataset is addressed by (object_id, digest) and is content-
//     addressed — identical (name, rows) collapses to the same version.
//     The name is stamped with a per-run Unix timestamp so every run
//     exercises the write path rather than resolving to an existing object.
//   - The read response's `rows` field is a *reference string* to the
//     underlying Table, not the row data. Parse the table digest out of
//     it and call /table/query. Rows come back wrapped as {digest, val,
//     original_index?} — the actual content lives under `val`.
//
// Run:
//   go run golang/10_create_dataset.go

package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"regexp"
	"strings"
	"time"
)

var (
	entity    = os.Getenv("WANDB_ENTITY")
	project   = os.Getenv("WANDB_PROJECT")
	projectID = entity + "/" + project
	baseURL   = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai")
	apiKey    = os.Getenv("WANDB_API_KEY")
	client    = &http.Client{}

	tableRefRe = regexp.MustCompile(`/table/([A-Za-z0-9_-]+)$`)
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

// do issues a request with auth, optionally with a JSON body, and decodes
// the JSON response. Any non-2xx is fatal. post/get wrap it.
func do(method, path string, body map[string]any) map[string]any {
	var reader io.Reader
	if body != nil {
		payload, err := json.Marshal(body)
		if err != nil {
			fatal("encode %s: %v", path, err)
		}
		reader = bytes.NewReader(payload)
	}
	req, err := http.NewRequest(method, baseURL+path, reader)
	if err != nil {
		fatal("request %s: %v", path, err)
	}
	req.SetBasicAuth("api", apiKey)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	res, err := client.Do(req)
	if err != nil {
		fatal("%s %s: %v", method, path, err)
	}
	defer res.Body.Close()
	raw, _ := io.ReadAll(res.Body)
	if res.StatusCode/100 != 2 {
		fatal("HTTP %d for %s %s: %s", res.StatusCode, method, path, raw)
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

func post(path string, body map[string]any) map[string]any { return do(http.MethodPost, path, body) }
func get(path string) map[string]any                       { return do(http.MethodGet, path, nil) }

// qaRow is a dataset row. Struct fields (not a map) so JSON preserves
// question-before-answer key order, matching the other language ports.
type qaRow struct {
	Question string `json:"question"`
	Answer   string `json:"answer"`
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

	datasetName := fmt.Sprintf("recipe-10-dataset-golang-%d", time.Now().Unix())
	datasetDescription := fmt.Sprintf("Capital cities for evaluation (run at %s)", time.Now().UTC().Format(time.RFC3339Nano))
	// Rows are sent as a struct (not a map) on purpose: encoding/json emits
	// struct fields in declaration order, so each row serializes as
	// {"question": ..., "answer": ...}. A map would sort keys alphabetically
	// (answer before question), producing a different byte-shape than the
	// other ports — and since the rows Table is content-addressed by row
	// bytes, a divergent shape pollutes the shared Table with duplicate rows.
	datasetRows := []qaRow{
		{"What is the capital of France?", "Paris"},
		{"What is the capital of Spain?", "Madrid"},
		{"What is the capital of Italy?", "Rome"},
	}

	// Create the Dataset. v2 path; entity + project go into the URL.
	created := post(fmt.Sprintf("/v2/%s/%s/datasets", entity, project), map[string]any{
		"name":        datasetName,
		"description": datasetDescription,
		"rows":        datasetRows,
	})
	objectID := created["object_id"].(string)
	digest := created["digest"].(string)
	fmt.Printf("Created: object_id=%s digest=%s… version=%v\n", objectID, digest[:12], created["version_index"])

	// Read Dataset metadata back. GET, with object_id + digest in the URL.
	dataset := get(fmt.Sprintf("/v2/%s/%s/datasets/%s/versions/%s", entity, project, objectID, digest))
	if dataset["name"] != datasetName {
		fatal("name: %v", dataset["name"])
	}
	if dataset["description"] != datasetDescription {
		fatal("description: %v", dataset["description"])
	}
	if dataset["object_id"] != objectID {
		fatal("object_id drift: %v", dataset["object_id"])
	}
	if dataset["digest"] != digest {
		fatal("digest drift: %v", dataset["digest"])
	}
	rowsRef := dataset["rows"].(string)
	fmt.Printf("Read:    name=%q rows_ref=%q\n", dataset["name"], rowsRef)

	// The rows field is a reference to a Table (weave:///.../table/{digest}).
	// Parse the table digest so we can /table/query it; tolerate a bare digest.
	tableDigest := rowsRef
	if m := tableRefRe.FindStringSubmatch(rowsRef); m != nil {
		tableDigest = m[1]
	}
	fmt.Printf("Table digest: %s…\n", tableDigest[:min(12, len(tableDigest))])

	// Query the actual rows.
	queried := post("/table/query", map[string]any{"project_id": projectID, "digest": tableDigest})
	rows, _ := queried["rows"].([]any)

	// --- verification ---
	// Row count + per-row content (under `val`) must match what we wrote.
	if len(rows) != len(datasetRows) {
		fatal("row count: %d vs %d", len(rows), len(datasetRows))
	}
	for i, expected := range datasetRows {
		row, _ := rows[i].(map[string]any)
		val, _ := row["val"].(map[string]any)
		if val["question"] != expected.Question || val["answer"] != expected.Answer {
			fatal("row %d val: %v vs %+v", i, row["val"], expected)
		}
	}
	first, _ := rows[0].(map[string]any)
	fmt.Printf("Verified: %d rows match (first: %v)\n", len(rows), first["val"])
}
