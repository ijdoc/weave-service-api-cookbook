///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

// Recipe 06: attach feedback to many Calls in one request.
//
// Demonstrates the bulk variant of /feedback/create:
//   POST /feedback/batch/create  -> N feedback items in one round trip
//
// Two wire-level points worth knowing:
//
//   - The path is /feedback/batch/create, not the more guessable
//     /feedback/create-batch or /feedback/createBatch.
//   - The body wraps a parallel-indexed array under `batch`:
//       {"batch": [<FeedbackCreateReq>, <FeedbackCreateReq>, ...]}
//     Each item carries its own project_id, weave_ref, feedback_type,
//     and payload — exactly the shape /feedback/create takes. The
//     response mirrors the input with {"res": [<FeedbackCreateRes>, ...]},
//     indices aligned to the input batch.
//
// This recipe creates three Calls and attaches two feedback items per
// Call in a single batch request: a wandb.note.1 (UI-visible in the
// trace table) and a custom scorer-style feedback. One round trip ships
// 6 items; the per-item endpoint would need 6. Mirrors recipe 05's
// note + scorer split, but bulk.
//
// Run:
//   jbang java/06_BatchFeedback.java

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

class BatchFeedback {
    static final ObjectMapper MAPPER = new ObjectMapper();
    static final HttpClient HTTP = HttpClient.newHttpClient();
    static final String BASE_URL = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai");
    static final String NOTE_TYPE = "wandb.note.1";
    static final String SCORER_TYPE = "recipe-06-scorer-correctness";
    static String projectId;
    static String authHeader;
    static Map<String, Object> baseAttributes;

    static String getenv(String key, String def) {
        String v = System.getenv(key);
        return (v == null || v.isEmpty()) ? def : v;
    }

    static void fail(String msg) {
        System.err.println("FAIL: " + msg);
        System.exit(1);
    }

    // Centralizes auth + JSON (de)serialization. Any non-2xx response is fatal.
    static JsonNode postJson(String path, Object body) throws Exception {
        String json = MAPPER.writeValueAsString(body);
        HttpRequest req = HttpRequest.newBuilder(URI.create(BASE_URL + path))
                .header("Authorization", authHeader)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(json, StandardCharsets.UTF_8))
                .build();
        HttpResponse<String> res = HTTP.send(req, HttpResponse.BodyHandlers.ofString());
        if (res.statusCode() / 100 != 2)
            fail("HTTP " + res.statusCode() + " for " + path + ": " + res.body());
        String b = res.body();
        return (b == null || b.isEmpty()) ? MAPPER.createObjectNode() : MAPPER.readTree(b);
    }

    static String startCall(String opName, Map<String, Object> inputs) throws Exception {
        JsonNode started = postJson("/call/start", Map.of(
                "start", Map.of(
                        "project_id", projectId,
                        "op_name", opName,
                        "started_at", Instant.now().toString(),
                        "attributes", baseAttributes,
                        "inputs", inputs)));
        return started.get("id").asText();
    }

    static void endCall(String callId, Map<String, Object> output) throws Exception {
        postJson("/call/end", Map.of(
                "end", Map.of(
                        "project_id", projectId,
                        "id", callId,
                        "ended_at", Instant.now().toString(),
                        "summary", Map.of(),
                        "output", output)));
    }

    public static void main(String[] args) throws Exception {
        var missing = new ArrayList<String>();
        for (String k : List.of("WANDB_API_KEY", "WANDB_ENTITY", "WANDB_PROJECT"))
            if (getenv(k, "").isEmpty()) missing.add(k);
        if (!missing.isEmpty())
            fail("Missing required env vars: " + String.join(", ", missing) + ". See ../README.md#setup.");

        projectId = System.getenv("WANDB_ENTITY") + "/" + System.getenv("WANDB_PROJECT");
        authHeader = "Basic " + Base64.getEncoder().encodeToString(
                ("api:" + System.getenv("WANDB_API_KEY")).getBytes(StandardCharsets.UTF_8));
        baseAttributes = new LinkedHashMap<>();
        baseAttributes.put("cookbook.language", "java");
        baseAttributes.put("cookbook.recipe", "06_batch_feedback");
        baseAttributes.put("cookbook.environment", getenv("COOKBOOK_ENVIRONMENT", "dev"));

        // Create three Calls — same shape as recipe 01, just repeated.
        String[][] questions = {
                {"What is the capital of France?", "Paris"},
                {"What is the capital of Spain?", "Madrid"},
                {"What is the capital of Italy?", "Rome"},
        };
        record Call(String id, String ref, String answer) {}
        var calls = new ArrayList<Call>();
        for (int i = 0; i < questions.length; i++) {
            String callId = startCall("recipe-06-call-" + (i + 1), Map.of("question", questions[i][0]));
            endCall(callId, Map.of("answer", questions[i][1]));
            calls.add(new Call(callId, "weave:///" + projectId + "/call/" + callId, questions[i][1]));
            System.out.println("Call " + (i + 1) + ": id=" + callId);
        }

        // Build the batch — note + scorer feedback per Call (6 items total).
        var batch = new ArrayList<Map<String, Object>>();
        for (Call c : calls) {
            batch.add(Map.of(
                    "project_id", projectId,
                    "weave_ref", c.ref(),
                    "feedback_type", NOTE_TYPE,
                    "payload", Map.of("note", "Reviewed — answer: '" + c.answer() + "'")));
            batch.add(Map.of(
                    "project_id", projectId,
                    "weave_ref", c.ref(),
                    "feedback_type", SCORER_TYPE,
                    "payload", Map.of("output", Map.of("score", 1.0, "reason", "Answer '" + c.answer() + "' matches expected"))));
        }

        // Single round trip for all six items.
        JsonNode resp = postJson("/feedback/batch/create", Map.of("batch", batch));
        JsonNode results = resp.path("res");
        if (results.size() != batch.size())
            fail("batch size mismatch: sent " + batch.size() + " got " + results.size());
        for (int i = 0; i < batch.size(); i++)
            System.out.println("Batch->Feedback: type=" + batch.get(i).get("feedback_type")
                    + " feedback_id=" + results.get(i).path("id").asText());

        // --- verification ---
        // For each Call, query feedback by weave_ref and assert both the note and
        // the scorer feedback landed with the expected payload. Brief retry
        // tolerates eventual consistency in the read path.
        List<String> expectedTypes = List.of(NOTE_TYPE, SCORER_TYPE);
        for (Call c : calls) {
            JsonNode expectedNote = MAPPER.valueToTree(Map.of("note", "Reviewed — answer: '" + c.answer() + "'"));
            JsonNode expectedScorer = MAPPER.valueToTree(Map.of("output", Map.of("score", 1.0, "reason", "Answer '" + c.answer() + "' matches expected")));
            var byType = new LinkedHashMap<String, JsonNode>();
            for (int i = 0; i < 5; i++) {
                JsonNode res = postJson("/feedback/query", Map.of(
                        "project_id", projectId,
                        "query", Map.of("$expr", Map.of("$eq", List.of(
                                Map.of("$getField", "weave_ref"),
                                Map.of("$literal", c.ref()))))));
                byType.clear();
                for (JsonNode row : res.path("result")) {
                    String t = row.path("feedback_type").asText(null);
                    if (t != null && expectedTypes.contains(t)) byType.put(t, row);
                }
                if (byType.keySet().containsAll(expectedTypes)) break;
                Thread.sleep(1000);
            }
            if (!byType.keySet().containsAll(expectedTypes))
                fail("feedback for " + c.ref() + " not all visible after 5 reads (got " + byType.keySet() + ")");
            if (!expectedNote.equals(byType.get(NOTE_TYPE).get("payload")))
                fail("note payload for " + c.id() + ": " + byType.get(NOTE_TYPE).get("payload"));
            if (!expectedScorer.equals(byType.get(SCORER_TYPE).get("payload")))
                fail("scorer payload for " + c.id() + ": " + byType.get(SCORER_TYPE).get("payload"));
            for (JsonNode row : byType.values())
                if (!c.ref().equals(row.path("weave_ref").asText()))
                    fail("weave_ref drift: " + row.get("weave_ref"));
        }
        System.out.println("Verified: " + batch.size() + " batched feedback items across " + calls.size() + " Calls (note + scorer each)");
    }
}
