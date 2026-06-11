// Recipe 13: query evaluation results.
//
// The "look at what already ran" recipe. Recipe 12 builds an evaluation
// run; recipe 13 aggregates across runs and walks the per-trial data —
// exactly what the W&B UI's Evaluations leaderboard view does. Pure
// read-only: it creates nothing.
//
// Two endpoint patterns combined:
//
//  1. /calls/stream_query with filter.op_names = [val.evaluate] and
//     filter.trace_roots_only = true — every root Call using the
//     canonical Evaluation.evaluate Op (NDJSON, one Call per line).
//  2. /v2/{entity}/{project}/eval_results/query with evaluation_call_ids
//     = [<root call ids>] — server-side aggregator that pulls each run's
//     predict_and_score / scorer children, computes per-scorer stats per
//     run, and (with include_rows) returns a row-major trial view.
//
// Wire-level points worth knowing:
//
//   - Filter by op_names with a full weave:// ref, not the short name.
//   - The canonical evaluate Op is shared across Eval Objects of the same
//     shape, so op_names alone returns runs across multiple Eval Objects;
//     narrow client-side with inputs.self starting with the object_id
//     prefix (matches any version of our Eval Object).
//   - summary.evaluations[] is one entry per run; rows[] is row-major
//     (keyed by row_digest, with a nested evaluations[].trials[]).
//
// Run:
//   go run golang/13_query_evaluation_results.go

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

const evalObjectID = "recipe-11-eval-golang"

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

func asString(v any) string {
	if s, ok := v.(string); ok {
		return s
	}
	return ""
}

func asInt(v any) int {
	if f, ok := v.(float64); ok {
		return int(f)
	}
	return 0
}

func asFloat(v any) float64 {
	if f, ok := v.(float64); ok {
		return f
	}
	return 0
}

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

// postNDJSON POSTs to a streaming endpoint and parses the NDJSON response
// (one JSON object per line) into rows.
func postNDJSON(path string, body map[string]any) []map[string]any {
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
	if res.StatusCode/100 != 2 {
		raw, _ := io.ReadAll(res.Body)
		fatal("HTTP %d for %s: %s", res.StatusCode, path, raw)
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
		fatal("read %s: %v", path, err)
	}
	return rows
}

func latestObject(objectID string) map[string]any {
	r := post("/objs/query", map[string]any{
		"project_id":    projectID,
		"filter":        map[string]any{"object_ids": []string{objectID}, "latest_only": true},
		"metadata_only": false,
	})
	objs, _ := r["objs"].([]any)
	if len(objs) == 0 {
		return nil
	}
	o, _ := objs[0].(map[string]any)
	return o
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

	// 1) Look up the Eval Object (recipe 11); we need val.evaluate to scope
	// the run search.
	evalObj := latestObject(evalObjectID)
	if evalObj == nil {
		fatal("FAIL: Evaluation Object `%s` not found. Run golang/11_create_evaluation.go first.", evalObjectID)
	}
	val, _ := evalObj["val"].(map[string]any)
	evaluateOpRef := val["evaluate"].(string)
	evalObjPrefix := fmt.Sprintf("weave:///%s/object/%s:", projectID, evalObjectID)
	fmt.Printf("Eval obj:   %s (latest digest=%s…)\n", evalObjectID, evalObj["digest"].(string)[:12])
	fmt.Printf("Op filter:  %s\n", evaluateOpRef)

	// 2) Find every root Call using this Evaluation.evaluate Op, then narrow
	// to runs against our Eval Object (any version) via the inputs.self prefix.
	// Retry: /calls/stream_query is eventually-consistent.
	var runs []map[string]any
	for i := 0; i < 8; i++ {
		roots := postNDJSON("/calls/stream_query", map[string]any{
			"project_id": projectID,
			"filter":     map[string]any{"trace_roots_only": true, "op_names": []string{evaluateOpRef}},
			"limit":      50,
			"sort_by":    []map[string]any{{"field": "started_at", "direction": "desc"}},
		})
		runs = nil
		for _, c := range roots {
			ins, _ := c["inputs"].(map[string]any)
			if strings.HasPrefix(asString(ins["self"]), evalObjPrefix) {
				runs = append(runs, c)
			}
		}
		if len(runs) > 0 {
			break
		}
		time.Sleep(time.Second)
	}
	if len(runs) == 0 {
		fatal("FAIL: no eval runs against `%s` found after 8 reads. Run golang/12_run_evaluation.go first.", evalObjectID)
	}
	fmt.Printf("Found:      %d run(s) against `%s` (any version)\n", len(runs), evalObjectID)

	// 3) Aggregate across all of them via /eval_results/query.
	callIDs := make([]string, len(runs))
	for i, c := range runs {
		callIDs[i] = c["id"].(string)
	}
	res := post(fmt.Sprintf("/v2/%s/%s/eval_results/query", entity, project), map[string]any{
		"evaluation_call_ids": callIDs,
		"include_rows":        true,
		"include_summary":     true,
	})
	totalRows := asInt(res["total_rows"])
	summary, _ := res["summary"].(map[string]any)
	evaluations, _ := summary["evaluations"].([]any)
	fmt.Printf("Aggregated: total_rows=%d, evaluations in summary=%d\n\n", totalRows, len(evaluations))

	// 4) Per-run leaderboard view.
	fmt.Println("RUNS (newest first):")
	fmt.Printf("  %-32s  %-20s  %6s  scorer summary\n", "display_name", "started_at", "trials")
	for _, e := range evaluations {
		ev, _ := e.(map[string]any)
		stats, _ := ev["scorer_stats"].([]any)
		var parts []string
		for _, s := range stats {
			st, _ := s.(map[string]any)
			parts = append(parts, fmt.Sprintf("%s=%d/%d (pass_rate=%.2f)",
				asString(st["scorer_key"]), asInt(st["pass_true_count"]), asInt(st["pass_known_count"]), asFloat(st["pass_rate"])))
		}
		started := asString(ev["started_at"])
		if len(started) > 19 {
			started = started[:19]
		}
		name := asString(ev["display_name"])
		if name == "" {
			name = "?"
		}
		fmt.Printf("  %-32s  %-20s  %6d  %s\n", name, started, asInt(ev["trial_count"]), strings.Join(parts, ", "))
	}

	// 5) Per-row drill-down: how the same dataset row was answered across runs.
	fmt.Println("\nROW 0 across all runs:")
	rows, _ := res["rows"].([]any)
	row0, _ := rows[0].(map[string]any)
	rowDigest := asString(row0["row_digest"])
	if len(rowDigest) > 16 {
		rowDigest = rowDigest[:16]
	}
	fmt.Printf("  row_digest=%s…\n", rowDigest)
	row0Evals, _ := row0["evaluations"].([]any)
	for _, rb := range row0Evals {
		runBlock, _ := rb.(map[string]any)
		callID := asString(runBlock["evaluation_call_id"])
		runLabel := "?"
		for _, e := range evaluations {
			ev, _ := e.(map[string]any)
			if asString(ev["evaluation_call_id"]) == callID {
				if n := asString(ev["display_name"]); n != "" {
					runLabel = n
				}
				break
			}
		}
		trials, _ := runBlock["trials"].([]any)
		for _, t := range trials {
			trial, _ := t.(map[string]any)
			scores, _ := trial["scores"].(map[string]any)
			var sp []string
			for k, v := range scores {
				sp = append(sp, fmt.Sprintf("%s=%v", k, v))
			}
			fmt.Printf("  - run=%-32s output=%-10v scores={%s}\n", runLabel, trial["model_output"], strings.Join(sp, ", "))
		}
	}

	// --- verification ---
	if totalRows <= 0 {
		fatal("expected total_rows > 0, got %d", totalRows)
	}
	if len(evaluations) == 0 {
		fatal("no evaluations in summary")
	}
	scorerKeys := map[string]bool{}
	for _, e := range evaluations {
		ev, _ := e.(map[string]any)
		stats, _ := ev["scorer_stats"].([]any)
		for _, s := range stats {
			st, _ := s.(map[string]any)
			scorerKeys[asString(st["scorer_key"])] = true
		}
	}
	scorersList, _ := val["scorers"].([]any)
	firstScorer := asString(scorersList[0])
	afterOp := firstScorer[strings.LastIndex(firstScorer, "/op/")+len("/op/"):]
	expectedScorerKey := strings.SplitN(afterOp, ":", 2)[0]
	if !scorerKeys[expectedScorerKey] {
		fatal("scorer key %q missing from %v — did recipe 12 use the canonical scorer-Op object_id as the scores-dict key?", expectedScorerKey, keys(scorerKeys))
	}
	if len(rows) == 0 {
		fatal("expected rows[] populated (include_rows=true)")
	}
	if len(row0Evals) == 0 {
		fatal("row 0 has no nested evaluations")
	}
	fmt.Printf("\nVerified:   %d trials across %d run(s); scorer_keys=%v\n", totalRows, len(evaluations), keys(scorerKeys))
}

func keys(m map[string]bool) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	return out
}
