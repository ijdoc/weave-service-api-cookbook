///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

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
//     it every run shows the bare op_name. Set to eval-java-<unix>.
//   - Root /call/end summary needs weave.status="success" and
//     status_counts.success = total call count (1 + N*3 + 1).
//   - The per-row `scores` key and the summarize/root output keys MUST be
//     the scorer Op's short name (object_id) — that's what links per-row
//     scorer_key back to the Eval Object's val.scorers and powers the
//     leaderboard. The model invocation is mocked (always returns the
//     expected answer), so pass_rate is 1.0.
//
// Run:
//   jbang java/12_RunEvaluation.java

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

class RunEvaluation {
    static final ObjectMapper MAPPER = new ObjectMapper();
    static final HttpClient HTTP = HttpClient.newHttpClient();
    static final String BASE_URL = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai");
    static final double MODEL_LATENCY = 0.001;
    static final Pattern DATASET_REF = Pattern.compile("weave:///[^/]+/[^/]+/object/([^:]+):(.+)");
    static final Pattern TABLE_REF = Pattern.compile("/table/([A-Za-z0-9_-]+)$");
    static String entity;
    static String project;
    static String projectId;
    static String authHeader;
    static Map<String, Object> attributes;

    static String getenv(String key, String def) {
        String v = System.getenv(key);
        return (v == null || v.isEmpty()) ? def : v;
    }

    static void fail(String msg) {
        System.err.println("FAIL: " + msg);
        System.exit(1);
    }

    static String now() { return Instant.now().toString(); }

    static JsonNode doReq(String method, String path, Object body) throws Exception {
        HttpRequest.Builder b = HttpRequest.newBuilder(URI.create(BASE_URL + path))
                .header("Authorization", authHeader);
        if (body != null) {
            b.header("Content-Type", "application/json")
                    .method(method, HttpRequest.BodyPublishers.ofString(MAPPER.writeValueAsString(body), StandardCharsets.UTF_8));
        } else {
            b.method(method, HttpRequest.BodyPublishers.noBody());
        }
        HttpResponse<String> res = HTTP.send(b.build(), HttpResponse.BodyHandlers.ofString());
        if (res.statusCode() / 100 != 2)
            fail("HTTP " + res.statusCode() + " for " + method + " " + path + ": " + res.body());
        String rb = res.body();
        return (rb == null || rb.isEmpty()) ? MAPPER.createObjectNode() : MAPPER.readTree(rb);
    }

    static JsonNode post(String path, Object body) throws Exception { return doReq("POST", path, body); }
    static JsonNode get(String path) throws Exception { return doReq("GET", path, null); }

    static JsonNode latestObject(String objectId) throws Exception {
        JsonNode r = post("/objs/query", Map.of(
                "project_id", projectId,
                "filter", Map.of("object_ids", List.of(objectId), "latest_only", true),
                "metadata_only", false));
        JsonNode objs = r.path("objs");
        return objs.isArray() && objs.size() > 0 ? objs.get(0) : null;
    }

    // Opens a Call; returns {id, trace_id}. parentId/traceId/displayName are
    // omitted when null.
    static String[] startCall(String opName, Map<String, Object> inputs, String parentId, String traceId, String displayName) throws Exception {
        var start = new LinkedHashMap<String, Object>();
        start.put("project_id", projectId);
        start.put("op_name", opName);
        start.put("started_at", now());
        start.put("attributes", attributes);
        start.put("inputs", inputs);
        if (parentId != null) start.put("parent_id", parentId);
        if (traceId != null) start.put("trace_id", traceId);
        if (displayName != null) start.put("display_name", displayName);
        JsonNode r = post("/call/start", Map.of("start", start));
        return new String[]{r.get("id").asText(), r.get("trace_id").asText()};
    }

    // Closes a Call with the default success summary.
    static void endCall(String callId, Object output) throws Exception {
        post("/call/end", Map.of("end", Map.of(
                "project_id", projectId,
                "id", callId,
                "ended_at", now(),
                "summary", Map.of(
                        "status_counts", Map.of("success", 1, "error", 0),
                        "weave", Map.of("status", "success")),
                "output", output)));
    }

    public static void main(String[] args) throws Exception {
        var missing = new ArrayList<String>();
        for (String k : List.of("WANDB_API_KEY", "WANDB_ENTITY", "WANDB_PROJECT"))
            if (getenv(k, "").isEmpty()) missing.add(k);
        if (!missing.isEmpty())
            fail("Missing required env vars: " + String.join(", ", missing) + ". See ../README.md#setup.");

        entity = System.getenv("WANDB_ENTITY");
        project = System.getenv("WANDB_PROJECT");
        projectId = entity + "/" + project;
        authHeader = "Basic " + Base64.getEncoder().encodeToString(
                ("api:" + System.getenv("WANDB_API_KEY")).getBytes(StandardCharsets.UTF_8));
        attributes = Map.of(
                "cookbook.language", "java",
                "cookbook.recipe", "12_run_evaluation",
                "cookbook.environment", getenv("COOKBOOK_ENVIRONMENT", "dev"));

        // 1) Look up the Evaluation Object + extract refs from its val.
        JsonNode evalObj = latestObject("recipe-11-eval-java");
        if (evalObj == null)
            fail("Evaluation Object `recipe-11-eval-java` not found. Run java/11_CreateEvaluation.java first.");
        String evalObjRef = "weave:///" + projectId + "/object/" + evalObj.get("object_id").asText() + ":" + evalObj.get("digest").asText();
        JsonNode ev = evalObj.get("val");
        String evaluateOpRef = ev.get("evaluate").asText();
        String predictAndScoreOpRef = ev.get("predict_and_score").asText();
        String summarizeOpRef = ev.get("summarize").asText();
        String scorerOpRef = ev.get("scorers").get(0).asText();
        String datasetRef = ev.get("dataset").asText();
        // The scorer Op's short_name (object_id) keys the per-row scores, the
        // wandb.runnable.* feedback_type, and the summarize/root output.
        String afterOp = scorerOpRef.substring(scorerOpRef.lastIndexOf("/op/") + "/op/".length());
        String scorerShortName = afterOp.split(":", 2)[0];
        System.out.println("Eval obj:  " + evalObj.get("object_id").asText() + " digest=" + evalObj.get("digest").asText().substring(0, 12) + "…");

        // 2) Look up the Model + its predict Op (recipe 08).
        JsonNode modelObj = latestObject("recipe-08-model-java");
        if (modelObj == null)
            fail("Model `recipe-08-model-java` not found. Run java/08_UseModel.java first.");
        String modelRef = "weave:///" + projectId + "/object/" + modelObj.get("object_id").asText() + ":" + modelObj.get("digest").asText();
        JsonNode modelPredictOp = latestObject("recipe-08-model-java.predict");
        if (modelPredictOp == null)
            fail("Model predict Op `recipe-08-model-java.predict` not found. Run java/08_UseModel.java first.");
        String modelPredictOpRef = "weave:///" + projectId + "/op/" + modelPredictOp.get("object_id").asText() + ":" + modelPredictOp.get("digest").asText();
        System.out.println("Model:     " + modelObj.get("object_id").asText() + " digest=" + modelObj.get("digest").asText().substring(0, 12) + "…");

        // 3) Walk the Dataset rows.
        Matcher dm = DATASET_REF.matcher(datasetRef);
        if (!dm.find()) fail("could not parse dataset_ref: " + datasetRef);
        String dsId = dm.group(1), dsDigest = dm.group(2);
        JsonNode dsMeta = get("/v2/" + entity + "/" + project + "/datasets/" + dsId + "/versions/" + dsDigest);
        String rowsRef = dsMeta.path("rows").asText();
        Matcher tm = TABLE_REF.matcher(rowsRef);
        String tableDigest = tm.find() ? tm.group(1) : rowsRef;
        JsonNode rowsRes = post("/table/query", Map.of("project_id", projectId, "digest", tableDigest));
        var rows = new ArrayList<JsonNode>();
        for (JsonNode rr : rowsRes.path("rows")) rows.add(rr.get("val"));
        System.out.println("Dataset:   " + dsId + " (" + rows.size() + " rows)");

        // 4) Build the 4-level Call trace.
        String displayName = "eval-java-" + Instant.now().getEpochSecond();
        String[] root = startCall(evaluateOpRef,
                Map.of("self", evalObjRef, "model", modelRef), null, null, displayName);
        String rootId = root[0], traceId = root[1];
        System.out.println("Root call: " + rootId + " (display_name=\"" + displayName + "\")");

        int nPass = 0, totalCalls = 1; // root
        for (JsonNode row : rows) {
            String[] ps = startCall(predictAndScoreOpRef,
                    Map.of("self", evalObjRef, "model", modelRef, "example", row), rootId, traceId, null);
            String psId = ps[0];

            // Predict child: invoke the (mocked) model — always returns expected.
            String[] pred = startCall(modelPredictOpRef,
                    Map.of("self", modelRef, "question", row.get("question").asText()), psId, traceId, null);
            String predId = pred[0];
            String prediction = row.get("answer").asText();
            endCall(predId, Map.of("answer", prediction));

            // Scorer child: compare prediction vs expected.
            String[] sc = startCall(scorerOpRef,
                    Map.of("output", prediction, "expected", row.get("answer").asText()), psId, traceId, null);
            String scId = sc[0];
            boolean score = prediction.equals(row.get("answer").asText());
            endCall(scId, score);

            // Link the score to the predict Call via a wandb.runnable.* Feedback
            // row (recipe 09's pattern) so the leaderboard attributes the scorer.
            post("/feedback/create", Map.of(
                    "project_id", projectId,
                    "weave_ref", "weave:///" + projectId + "/call/" + predId,
                    "feedback_type", "wandb.runnable." + scorerShortName,
                    "payload", Map.of("output", score),
                    "runnable_ref", scorerOpRef,
                    "call_ref", "weave:///" + projectId + "/call/" + scId));

            // End predict_and_score. The `scores` key MUST be the scorer Op's
            // short name so /eval_results/query links it to val.scorers.
            endCall(psId, Map.of(
                    "output", prediction,
                    "scores", Map.of(scorerShortName, score),
                    "model_latency", MODEL_LATENCY));

            if (score) nPass++;
            totalCalls += 3; // predict_and_score + predict + scorer
        }

        // Summarize: sibling of predict_and_score under the root.
        String[] sum = startCall(summarizeOpRef, Map.of("self", evalObjRef), rootId, traceId, null);
        double passRate = rows.isEmpty() ? 0.0 : (double) nPass / rows.size();
        // summarize.output AND root.output are keyed by the scorer short name
        // (matching val.scorers + per-row scorer_key) plus model_latency.mean.
        Map<String, Object> aggregatedOutput = Map.of(
                scorerShortName, Map.of("true_count", nPass, "true_fraction", passRate),
                "model_latency", Map.of("mean", MODEL_LATENCY));
        endCall(sum[0], aggregatedOutput);
        totalCalls++; // summarize

        // 5) End the root with the proper summary shape.
        post("/call/end", Map.of("end", Map.of(
                "project_id", projectId,
                "id", rootId,
                "ended_at", now(),
                "summary", Map.of(
                        "status_counts", Map.of("success", totalCalls, "error", 0),
                        "weave", Map.of("status", "success", "display_name", displayName)),
                "output", aggregatedOutput)));
        System.out.printf("Trace done: %d calls, pass_rate=%.2f%n", totalCalls, passRate);

        // --- verification ---
        // /eval_results/query with the root call_id aggregates per-row trial
        // data + scorer stats; evaluation_ref should match our Eval Object.
        Thread.sleep(2000);
        JsonNode results = null;
        for (int i = 0; i < 8; i++) {
            JsonNode r = post("/v2/" + entity + "/" + project + "/eval_results/query", Map.of(
                    "evaluation_call_ids", List.of(rootId),
                    "include_rows", true,
                    "include_summary", true));
            if (r.path("total_rows").asInt(-1) == rows.size()) {
                results = r;
                break;
            }
            Thread.sleep(1000);
        }
        if (results == null)
            fail("eval_results/query did not return " + rows.size() + " rows after 8 attempts");

        JsonNode evals = results.path("summary").path("evaluations");
        if (evals.size() != 1) fail("expected 1 evaluation in summary, got " + evals.size());
        JsonNode evSummary = evals.get(0);
        if (!evalObjRef.equals(evSummary.path("evaluation_ref").asText()))
            fail("evaluation_ref: " + evSummary.get("evaluation_ref"));
        var scorerKeys = new ArrayList<String>();
        for (JsonNode s : evSummary.path("scorer_stats")) scorerKeys.add(s.path("scorer_key").asText());
        if (!scorerKeys.contains(scorerShortName))
            fail("'" + scorerShortName + "' missing from scorer_stats: " + scorerKeys);
        System.out.println("Verified:  /eval_results/query returned " + results.path("total_rows").asInt()
                + " rows, evaluation_ref matches, scorer_stats=" + scorerKeys);
    }
}
