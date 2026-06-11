///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

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
//   jbang java/13_QueryEvaluationResults.java

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Iterator;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;
import java.util.stream.Stream;

class QueryEvaluationResults {
    static final ObjectMapper MAPPER = new ObjectMapper();
    static final HttpClient HTTP = HttpClient.newHttpClient();
    static final String BASE_URL = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai");
    static final String EVAL_OBJECT_ID = "recipe-11-eval-java";
    static String entity;
    static String project;
    static String projectId;
    static String authHeader;

    static String getenv(String key, String def) {
        String v = System.getenv(key);
        return (v == null || v.isEmpty()) ? def : v;
    }

    static void fail(String msg) {
        System.err.println("FAIL: " + msg);
        System.exit(1);
    }

    static JsonNode post(String path, Object body) throws Exception {
        HttpRequest req = HttpRequest.newBuilder(URI.create(BASE_URL + path))
                .header("Authorization", authHeader)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(MAPPER.writeValueAsString(body), StandardCharsets.UTF_8))
                .build();
        HttpResponse<String> res = HTTP.send(req, HttpResponse.BodyHandlers.ofString());
        if (res.statusCode() / 100 != 2)
            fail("HTTP " + res.statusCode() + " for " + path + ": " + res.body());
        String rb = res.body();
        return (rb == null || rb.isEmpty()) ? MAPPER.createObjectNode() : MAPPER.readTree(rb);
    }

    // POSTs to a streaming endpoint and parses the NDJSON response (one JSON
    // object per line) into a list.
    static List<JsonNode> postNDJSON(String path, Object body) throws Exception {
        HttpRequest req = HttpRequest.newBuilder(URI.create(BASE_URL + path))
                .header("Authorization", authHeader)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(MAPPER.writeValueAsString(body), StandardCharsets.UTF_8))
                .build();
        HttpResponse<Stream<String>> res = HTTP.send(req, HttpResponse.BodyHandlers.ofLines());
        if (res.statusCode() / 100 != 2)
            fail("HTTP " + res.statusCode() + " for " + path + ": " + res.body().collect(Collectors.joining("\n")));
        var rows = new ArrayList<JsonNode>();
        for (Iterator<String> it = res.body().iterator(); it.hasNext(); ) {
            String line = it.next();
            if (line.isBlank()) continue;
            rows.add(MAPPER.readTree(line));
        }
        return rows;
    }

    static JsonNode latestObject(String objectId) throws Exception {
        JsonNode r = post("/objs/query", Map.of(
                "project_id", projectId,
                "filter", Map.of("object_ids", List.of(objectId), "latest_only", true),
                "metadata_only", false));
        JsonNode objs = r.path("objs");
        return objs.isArray() && objs.size() > 0 ? objs.get(0) : null;
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

        // 1) Look up the Eval Object (recipe 11); we need val.evaluate.
        JsonNode evalObj = latestObject(EVAL_OBJECT_ID);
        if (evalObj == null)
            fail("Evaluation Object `" + EVAL_OBJECT_ID + "` not found. Run java/11_CreateEvaluation.java first.");
        JsonNode val = evalObj.get("val");
        String evaluateOpRef = val.get("evaluate").asText();
        String evalObjPrefix = "weave:///" + projectId + "/object/" + EVAL_OBJECT_ID + ":";
        System.out.println("Eval obj:   " + EVAL_OBJECT_ID + " (latest digest=" + evalObj.get("digest").asText().substring(0, 12) + "…)");
        System.out.println("Op filter:  " + evaluateOpRef);

        // 2) Find every root Call using this evaluate Op, narrow to runs
        // against our Eval Object via inputs.self prefix. Retry (eventually
        // consistent).
        var runs = new ArrayList<JsonNode>();
        for (int i = 0; i < 8; i++) {
            List<JsonNode> roots = postNDJSON("/calls/stream_query", Map.of(
                    "project_id", projectId,
                    "filter", Map.of("trace_roots_only", true, "op_names", List.of(evaluateOpRef)),
                    "limit", 50,
                    "sort_by", List.of(Map.of("field", "started_at", "direction", "desc"))));
            runs.clear();
            for (JsonNode c : roots)
                if (c.path("inputs").path("self").asText("").startsWith(evalObjPrefix))
                    runs.add(c);
            if (!runs.isEmpty()) break;
            Thread.sleep(1000);
        }
        if (runs.isEmpty())
            fail("no eval runs against `" + EVAL_OBJECT_ID + "` found after 8 reads. Run java/12_RunEvaluation.java first.");
        System.out.println("Found:      " + runs.size() + " run(s) against `" + EVAL_OBJECT_ID + "` (any version)");

        // 3) Aggregate across all of them via /eval_results/query.
        var callIds = new ArrayList<String>();
        for (JsonNode c : runs) callIds.add(c.get("id").asText());
        JsonNode res = post("/v2/" + entity + "/" + project + "/eval_results/query", Map.of(
                "evaluation_call_ids", callIds,
                "include_rows", true,
                "include_summary", true));
        int totalRows = res.path("total_rows").asInt();
        JsonNode evaluations = res.path("summary").path("evaluations");
        System.out.println("Aggregated: total_rows=" + totalRows + ", evaluations in summary=" + evaluations.size() + "\n");

        // 4) Per-run leaderboard view.
        System.out.println("RUNS (newest first):");
        System.out.printf("  %-32s  %-20s  %6s  scorer summary%n", "display_name", "started_at", "trials");
        for (JsonNode ev : evaluations) {
            var parts = new ArrayList<String>();
            for (JsonNode s : ev.path("scorer_stats"))
                parts.add(String.format("%s=%d/%d (pass_rate=%.2f)",
                        s.path("scorer_key").asText(), s.path("pass_true_count").asInt(),
                        s.path("pass_known_count").asInt(), s.path("pass_rate").asDouble()));
            String started = ev.path("started_at").asText("");
            if (started.length() > 19) started = started.substring(0, 19);
            String name = ev.path("display_name").asText("?");
            System.out.printf("  %-32s  %-20s  %6d  %s%n", name, started, ev.path("trial_count").asInt(), String.join(", ", parts));
        }

        // 5) Per-row drill-down: how the same dataset row was answered across runs.
        System.out.println("\nROW 0 across all runs:");
        JsonNode rows = res.path("rows");
        JsonNode row0 = rows.get(0);
        String rowDigest = row0.path("row_digest").asText("");
        if (rowDigest.length() > 16) rowDigest = rowDigest.substring(0, 16);
        System.out.println("  row_digest=" + rowDigest + "…");
        for (JsonNode runBlock : row0.path("evaluations")) {
            String callId = runBlock.path("evaluation_call_id").asText();
            String runLabel = "?";
            for (JsonNode ev : evaluations)
                if (ev.path("evaluation_call_id").asText().equals(callId)) {
                    runLabel = ev.path("display_name").asText("?");
                    break;
                }
            for (JsonNode trial : runBlock.path("trials")) {
                var sp = new ArrayList<String>();
                JsonNode scores = trial.path("scores");
                for (Iterator<String> it = scores.fieldNames(); it.hasNext(); ) {
                    String k = it.next();
                    sp.add(k + "=" + scores.get(k));
                }
                System.out.printf("  - run=%-32s output=%-10s scores={%s}%n", runLabel, trial.path("model_output"), String.join(", ", sp));
            }
        }

        // --- verification ---
        if (totalRows <= 0) fail("expected total_rows > 0, got " + totalRows);
        if (evaluations.size() == 0) fail("no evaluations in summary");
        var scorerKeys = new LinkedHashSet<String>();
        for (JsonNode ev : evaluations)
            for (JsonNode s : ev.path("scorer_stats"))
                scorerKeys.add(s.path("scorer_key").asText());
        String firstScorer = val.path("scorers").get(0).asText();
        String afterOp = firstScorer.substring(firstScorer.lastIndexOf("/op/") + "/op/".length());
        String expectedScorerKey = afterOp.split(":", 2)[0];
        if (!scorerKeys.contains(expectedScorerKey))
            fail("scorer key '" + expectedScorerKey + "' missing from " + scorerKeys
                    + " — did recipe 12 use the canonical scorer-Op object_id as the scores-dict key?");
        if (rows.size() == 0) fail("expected rows[] populated (include_rows=true)");
        if (row0.path("evaluations").size() == 0) fail("row 0 has no nested evaluations");
        System.out.println("\nVerified:   " + totalRows + " trials across " + evaluations.size() + " run(s); scorer_keys=" + scorerKeys);
    }
}
