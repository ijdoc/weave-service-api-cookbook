///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

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
// naming (recipe-09-is-correct-java) is fine. The Scorer Op's source
// carries the ADR-0004 scaffold.
//
// Run:
//   jbang java/09_ScoreACall.java

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Base64;
import java.util.List;
import java.util.Map;

class ScoreACall {
    static final ObjectMapper MAPPER = new ObjectMapper();
    static final HttpClient HTTP = HttpClient.newHttpClient();
    static final String BASE_URL = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai");
    static final String RECIPE_PATH = "java/09_ScoreACall.java";
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

    // Centralizes auth + JSON (de)serialization. Any non-2xx response is fatal.
    static JsonNode post(String path, Object body) throws Exception {
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

        Map<String, Object> attributes = Map.of(
                "cookbook.language", "java",
                "cookbook.recipe", "09_score_a_call",
                "cookbook.environment", getenv("COOKBOOK_ENVIRONMENT", "dev"));

        // --- ADR-0004 scaffold for the Scorer Op ---
        byte[] recipeBytes = Files.readAllBytes(Path.of(RECIPE_PATH));
        byte[] hash = MessageDigest.getInstance("SHA-256").digest(recipeBytes);
        StringBuilder hex = new StringBuilder();
        for (byte b : hash) hex.append(String.format("%02x", b));
        String recipeSha = hex.substring(0, 16);

        String scorerSource = """
                # Cookbook scaffold (java)
                # Source: %s
                # SHA256: %s

                import weave


                @weave.op
                def is_correct(output, expected):
                    \"\"\"The actual scoring implementation lives in:
                        %s

                    Byte-for-byte reference (SHA256 of the recipe file):
                        %s

                    To verify a local copy of the file matches (POSIX shell):
                        shasum -a 256 %s | cut -c1-16

                    This Python op is a metadata handle, not the real scorer — running
                    it raises NotImplementedError by design.
                    \"\"\"
                    raise NotImplementedError(
                        "This op is a Python scaffold uploaded from a non-Python recipe. "
                        "See the docstring above for the real source-language file and a "
                        "verifiable byte-for-byte reference (SHA256)."
                    )
                """.formatted(RECIPE_PATH, recipeSha, RECIPE_PATH, recipeSha, RECIPE_PATH);

        // 1) Register the Scorer Op. Per-language object_id; the server
        // lowercases it. Scorer Op names are not aggregator-filtered.
        String scorerOpId = "recipe-09-is-correct-java";
        JsonNode scorerRes = post("/v2/" + entity + "/" + project + "/ops", Map.of(
                "name", scorerOpId,
                "source_code", scorerSource));
        String scorerOpRef = "weave:///" + projectId + "/op/" + scorerRes.get("object_id").asText() + ":" + scorerRes.get("digest").asText();
        System.out.println("Scorer op:  " + scorerRes.get("object_id").asText()
                + " digest=" + scorerRes.get("digest").asText().substring(0, 12)
                + "… version=" + scorerRes.path("version_index").asText());

        // 2) Produce a sample prediction via a tiny model Call (op_name is a
        // plain string here — no Model/predict Op).
        String question = "Is the sky blue?";
        String expected = "yes";
        String prediction = "yes";
        JsonNode started = post("/call/start", Map.of(
                "start", Map.of(
                        "project_id", projectId,
                        "op_name", "recipe-09-mock-predict",
                        "started_at", Instant.now().toString(),
                        "attributes", attributes,
                        "inputs", Map.of("question", question))));
        String predictCallId = started.get("id").asText();
        post("/call/end", Map.of(
                "end", Map.of(
                        "project_id", projectId,
                        "id", predictCallId,
                        "ended_at", Instant.now().toString(),
                        "summary", Map.of(
                                "status_counts", Map.of("success", 1, "error", 0),
                                "weave", Map.of("status", "success", "trace_name", "recipe-09-mock-predict")),
                        // Per the cookbook's question/answer convention, predict
                        // outputs land under an `answer` key; the Scorer Op takes
                        // the raw answer value as its `output` argument.
                        "output", Map.of("answer", prediction))));
        System.out.println("Predicted:  id=" + predictCallId + " output=\"" + prediction + "\"");

        // 3) Open a scoring Call invoking the Scorer Op. op_name MUST be the
        // Op's weave:// ref; output is the score value (boolean).
        JsonNode startedScore = post("/call/start", Map.of(
                "start", Map.of(
                        "project_id", projectId,
                        "op_name", scorerOpRef,
                        "started_at", Instant.now().toString(),
                        "attributes", attributes,
                        "inputs", Map.of("output", prediction, "expected", expected))));
        String scoreCallId = startedScore.get("id").asText();
        boolean score = prediction.equals(expected);
        post("/call/end", Map.of(
                "end", Map.of(
                        "project_id", projectId,
                        "id", scoreCallId,
                        "ended_at", Instant.now().toString(),
                        "summary", Map.of(
                                "status_counts", Map.of("success", 1, "error", 0),
                                "weave", Map.of("status", "success", "trace_name", scorerOpId)),
                        "output", score)));
        System.out.println("Scored:     id=" + scoreCallId + " output=" + score);

        // 4) Link the score to the prediction Call via a wandb.runnable.<id>
        // Feedback row on the prediction.
        String predictCallRef = "weave:///" + projectId + "/call/" + predictCallId;
        String scoreCallRef = "weave:///" + projectId + "/call/" + scoreCallId;
        String feedbackType = "wandb.runnable." + scorerOpId;
        JsonNode feedbackRes = post("/feedback/create", Map.of(
                "project_id", projectId,
                "weave_ref", predictCallRef,
                "feedback_type", feedbackType,
                "payload", Map.of("output", score),
                "runnable_ref", scorerOpRef,
                "call_ref", scoreCallRef));
        System.out.println("Linked:     feedback id=" + feedbackRes.path("id").asText()
                + " on predict call (feedback_type=" + feedbackType + ")");

        // --- verification ---
        // (a) The scoring Call round-trips with the right op_ref + inputs + output.
        JsonNode call = null;
        for (int i = 0; i < 5; i++) {
            JsonNode res = post("/call/read", Map.of("project_id", projectId, "id", scoreCallId));
            call = res.get("call");
            if (call != null && !call.isNull() && call.hasNonNull("ended_at")) break;
            Thread.sleep(1000);
        }
        if (call == null || call.isNull() || !call.hasNonNull("ended_at"))
            fail("scoring Call " + scoreCallId + " not visible/finished after 5 reads");
        if (!scorerOpRef.equals(call.path("op_name").asText()))
            fail("op_name: " + call.get("op_name"));
        if (!prediction.equals(call.path("inputs").path("output").asText()))
            fail("inputs.output: " + call.path("inputs").get("output"));
        if (!expected.equals(call.path("inputs").path("expected").asText()))
            fail("inputs.expected: " + call.path("inputs").get("expected"));
        if (call.path("output").asBoolean() != score)
            fail("output: " + call.get("output"));

        // (b) The wandb.runnable.* Feedback row exists on the prediction Call.
        JsonNode linking = null;
        for (int i = 0; i < 5 && linking == null; i++) {
            JsonNode res = post("/feedback/query", Map.of(
                    "project_id", projectId,
                    "query", Map.of("$expr", Map.of("$eq", List.of(
                            Map.of("$getField", "weave_ref"),
                            Map.of("$literal", predictCallRef))))));
            for (JsonNode row : res.path("result")) {
                if (feedbackType.equals(row.path("feedback_type").asText())) {
                    linking = row;
                    break;
                }
            }
            if (linking == null) Thread.sleep(1000);
        }
        if (linking == null)
            fail("no '" + feedbackType + "' feedback on " + predictCallRef + " after 5 reads");
        if (!MAPPER.valueToTree(Map.of("output", score)).equals(linking.get("payload")))
            fail("payload: " + linking.get("payload"));
        if (!scorerOpRef.equals(linking.path("runnable_ref").asText()))
            fail("runnable_ref: " + linking.get("runnable_ref"));
        if (!scoreCallRef.equals(linking.path("call_ref").asText()))
            fail("call_ref: " + linking.get("call_ref"));
        System.out.println("Verified:   id=" + scoreCallId + " (scorer op + inputs + score output round-tripped)");
        System.out.println("Verified:   " + feedbackType + " feedback links score -> predict");
    }
}
