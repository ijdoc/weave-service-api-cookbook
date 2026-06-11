// Recipe 12: run an evaluation as a 4-level Call trace.
//
// The integration recipe. Looks up everything earlier recipes created,
// builds the structured Call tree the W&B UI recognises as an evaluation
// run, and verifies via /eval_results/query. Lands ADR-0005 (the
// imperative-SDK-path decision).
//
// The trace shape mirrors the SDK's evaluation.evaluate(model):
//
//   Evaluation.evaluate                  (root, op_name = canonical ref)
//   |-- Evaluation.predict_and_score     (per-row trial)
//   |   |-- <Model>.predict              (the model invocation)
//   |   `-- <scorer>                     (scoring)
//   |-- ... (one predict_and_score per row)
//   `-- Evaluation.summarize             (sibling of predict_and_score)
//
// This recipe *creates only Calls* — recipe 11 owns the eval's definition
// (Eval Object + canonical Ops); recipe 08 owns the Model + predict Op.
//
// Wire-level points worth knowing:
//
//   - Per-Call op_name MUST be a weave:// URI to an existing Op.
//   - The root Call's display_name is the Evaluations-page label; without
//     it every run shows the bare op_name. Set to eval-golang-<unix>.
//   - Root /call/end summary needs weave.status="success" and
//     status_counts.success = total call count (1 + N*3 + 1).
//   - The per-row `scores` key and the summarize/root output keys MUST be
//     the scorer Op's short name (object_id) — that's what links per-row
//     scorer_key back to the Eval Object's val.scorers and powers the
//     leaderboard. The model invocation is mocked (always returns the
//     expected answer), so pass_rate is 1.0.
//
// Run:
//   go run golang/12_run_evaluation.go

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

const modelLatency = 0.001

var (
	entity    = os.Getenv("WANDB_ENTITY")
	project   = os.Getenv("WANDB_PROJECT")
	projectID = entity + "/" + project
	baseURL   = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai")
	apiKey    = os.Getenv("WANDB_API_KEY")
	client    = &http.Client{}

	attributes = map[string]any{
		"cookbook.language":    "golang",
		"cookbook.recipe":      "12_run_evaluation",
		"cookbook.environment": getenv("COOKBOOK_ENVIRONMENT", "dev"),
	}
	datasetRefRe = regexp.MustCompile(`weave:///[^/]+/[^/]+/object/([^:]+):(.+)`)
	tableRefRe   = regexp.MustCompile(`/table/([A-Za-z0-9_-]+)$`)
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

func now() string { return time.Now().UTC().Format(time.RFC3339Nano) }

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

// startCall opens a Call; parentID/traceID/displayName are omitted when "".
func startCall(opName string, inputs map[string]any, parentID, traceID, displayName string) (string, string) {
	start := map[string]any{
		"project_id": projectID,
		"op_name":    opName,
		"started_at": now(),
		"attributes": attributes,
		"inputs":     inputs,
	}
	if parentID != "" {
		start["parent_id"] = parentID
	}
	if traceID != "" {
		start["trace_id"] = traceID
	}
	if displayName != "" {
		start["display_name"] = displayName
	}
	r := post("/call/start", map[string]any{"start": start})
	return r["id"].(string), r["trace_id"].(string)
}

// endCall closes a Call with the default success summary.
func endCall(callID string, output any) {
	post("/call/end", map[string]any{"end": map[string]any{
		"project_id": projectID,
		"id":         callID,
		"ended_at":   now(),
		"summary": map[string]any{
			"status_counts": map[string]any{"success": 1, "error": 0},
			"weave":         map[string]any{"status": "success"},
		},
		"output": output,
	}})
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

	// 1) Look up the Evaluation Object + extract refs from its val.
	evalObj := latestObject("recipe-11-eval-golang")
	if evalObj == nil {
		fatal("FAIL: Evaluation Object `recipe-11-eval-golang` not found. Run golang/11_create_evaluation.go first.")
	}
	evalObjRef := fmt.Sprintf("weave:///%s/object/%s:%s", projectID, evalObj["object_id"], evalObj["digest"])
	ev, _ := evalObj["val"].(map[string]any)
	evaluateOpRef := ev["evaluate"].(string)
	predictAndScoreOpRef := ev["predict_and_score"].(string)
	summarizeOpRef := ev["summarize"].(string)
	scorers, _ := ev["scorers"].([]any)
	scorerOpRef := scorers[0].(string)
	datasetRef := ev["dataset"].(string)
	// The scorer Op's short_name (object_id) keys the per-row scores, the
	// wandb.runnable.* feedback_type, and the summarize/root output.
	afterOp := scorerOpRef[strings.LastIndex(scorerOpRef, "/op/")+len("/op/"):]
	scorerShortName := strings.SplitN(afterOp, ":", 2)[0]
	fmt.Printf("Eval obj:  %v digest=%v…\n", evalObj["object_id"], evalObj["digest"].(string)[:12])

	// 2) Look up the Model + its predict Op (recipe 08).
	modelObj := latestObject("recipe-08-model-golang")
	if modelObj == nil {
		fatal("FAIL: Model `recipe-08-model-golang` not found. Run golang/08_use_model.go first.")
	}
	modelRef := fmt.Sprintf("weave:///%s/object/%s:%s", projectID, modelObj["object_id"], modelObj["digest"])
	modelPredictOp := latestObject("recipe-08-model-golang.predict")
	if modelPredictOp == nil {
		fatal("FAIL: Model predict Op `recipe-08-model-golang.predict` not found. Run golang/08_use_model.go first.")
	}
	modelPredictOpRef := fmt.Sprintf("weave:///%s/op/%s:%s", projectID, modelPredictOp["object_id"], modelPredictOp["digest"])
	fmt.Printf("Model:     %v digest=%v…\n", modelObj["object_id"], modelObj["digest"].(string)[:12])

	// 3) Walk the Dataset rows.
	m := datasetRefRe.FindStringSubmatch(datasetRef)
	if m == nil {
		fatal("FAIL: could not parse dataset_ref: %q", datasetRef)
	}
	dsID, dsDigest := m[1], m[2]
	dsMeta := get(fmt.Sprintf("/v2/%s/%s/datasets/%s/versions/%s", entity, project, dsID, dsDigest))
	rowsRef := dsMeta["rows"].(string)
	tableDigest := rowsRef
	if tm := tableRefRe.FindStringSubmatch(rowsRef); tm != nil {
		tableDigest = tm[1]
	}
	rowsRes := post("/table/query", map[string]any{"project_id": projectID, "digest": tableDigest})
	rawRows, _ := rowsRes["rows"].([]any)
	var rows []map[string]any
	for _, rr := range rawRows {
		if r, ok := rr.(map[string]any); ok {
			rows = append(rows, r["val"].(map[string]any))
		}
	}
	fmt.Printf("Dataset:   %s (%d rows)\n", dsID, len(rows))

	// 4) Build the 4-level Call trace. The display_name on the root is the
	// Evaluations-page label.
	displayName := fmt.Sprintf("eval-golang-%d", time.Now().Unix())
	rootID, traceID := startCall(evaluateOpRef,
		map[string]any{"self": evalObjRef, "model": modelRef}, "", "", displayName)
	fmt.Printf("Root call: %s (display_name=%q)\n", rootID, displayName)

	nPass := 0
	totalCalls := 1 // root
	for _, row := range rows {
		psID, _ := startCall(predictAndScoreOpRef,
			map[string]any{"self": evalObjRef, "model": modelRef, "example": row}, rootID, traceID, "")

		// Predict child: invoke the (mocked) model — always returns expected.
		predID, _ := startCall(modelPredictOpRef,
			map[string]any{"self": modelRef, "question": row["question"]}, psID, traceID, "")
		prediction := row["answer"]
		endCall(predID, map[string]any{"answer": prediction})

		// Scorer child: compare prediction vs expected.
		scID, _ := startCall(scorerOpRef,
			map[string]any{"output": prediction, "expected": row["answer"]}, psID, traceID, "")
		score := prediction == row["answer"]
		endCall(scID, score)

		// Link the score to the predict Call via a wandb.runnable.* Feedback
		// row (recipe 09's pattern) so the leaderboard attributes the scorer Op.
		predCallRef := fmt.Sprintf("weave:///%s/call/%s", projectID, predID)
		scoreCallRef := fmt.Sprintf("weave:///%s/call/%s", projectID, scID)
		post("/feedback/create", map[string]any{
			"project_id":    projectID,
			"weave_ref":     predCallRef,
			"feedback_type": "wandb.runnable." + scorerShortName,
			"payload":       map[string]any{"output": score},
			"runnable_ref":  scorerOpRef,
			"call_ref":      scoreCallRef,
		})

		// End predict_and_score. The `scores` key MUST be the scorer Op's
		// short name so /eval_results/query links it to val.scorers.
		endCall(psID, map[string]any{
			"output":        prediction,
			"scores":        map[string]any{scorerShortName: score},
			"model_latency": modelLatency,
		})

		if score {
			nPass++
		}
		totalCalls += 3 // predict_and_score + predict + scorer
	}

	// Summarize: sibling of predict_and_score under the root.
	sumID, _ := startCall(summarizeOpRef, map[string]any{"self": evalObjRef}, rootID, traceID, "")
	passRate := 0.0
	if len(rows) > 0 {
		passRate = float64(nPass) / float64(len(rows))
	}
	// summarize.output AND root.output are keyed by the scorer short name
	// (matching val.scorers + the per-row scorer_key) plus model_latency.mean
	// — this is what the leaderboard view buckets across runs.
	aggregatedOutput := map[string]any{
		scorerShortName: map[string]any{"true_count": nPass, "true_fraction": passRate},
		"model_latency": map[string]any{"mean": modelLatency},
	}
	endCall(sumID, aggregatedOutput)
	totalCalls++ // summarize

	// 5) End the root with the proper summary shape.
	post("/call/end", map[string]any{"end": map[string]any{
		"project_id": projectID,
		"id":         rootID,
		"ended_at":   now(),
		"summary": map[string]any{
			"status_counts": map[string]any{"success": totalCalls, "error": 0},
			"weave":         map[string]any{"status": "success", "display_name": displayName},
		},
		"output": aggregatedOutput,
	}})
	fmt.Printf("Trace done: %d calls, pass_rate=%.2f\n", totalCalls, passRate)

	// --- verification ---
	// /eval_results/query with the root call_id aggregates per-row trial data
	// + scorer stats. The summary's evaluation_ref should match our Eval Object.
	time.Sleep(2 * time.Second)
	var results map[string]any
	for i := 0; i < 8; i++ {
		r := post(fmt.Sprintf("/v2/%s/%s/eval_results/query", entity, project), map[string]any{
			"evaluation_call_ids": []string{rootID},
			"include_rows":        true,
			"include_summary":     true,
		})
		if tr, ok := r["total_rows"].(float64); ok && int(tr) == len(rows) {
			results = r
			break
		}
		time.Sleep(time.Second)
	}
	if results == nil {
		fatal("FAIL: eval_results/query did not return %d rows after 8 attempts", len(rows))
	}

	summary, _ := results["summary"].(map[string]any)
	evals, _ := summary["evaluations"].([]any)
	if len(evals) != 1 {
		fatal("expected 1 evaluation in summary, got %d", len(evals))
	}
	evSummary, _ := evals[0].(map[string]any)
	if evSummary["evaluation_ref"] != evalObjRef {
		fatal("evaluation_ref: %v", evSummary["evaluation_ref"])
	}
	var scorerKeys []string
	stats, _ := evSummary["scorer_stats"].([]any)
	for _, s := range stats {
		if sm, ok := s.(map[string]any); ok {
			if k, ok := sm["scorer_key"].(string); ok {
				scorerKeys = append(scorerKeys, k)
			}
		}
	}
	found := false
	for _, k := range scorerKeys {
		if k == scorerShortName {
			found = true
		}
	}
	if !found {
		fatal("%q missing from scorer_stats: %v", scorerShortName, scorerKeys)
	}
	fmt.Printf("Verified:  /eval_results/query returned %v rows, evaluation_ref matches, scorer_stats=%v\n", results["total_rows"], scorerKeys)
}
