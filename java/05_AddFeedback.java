///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

// Recipe 05: attach feedback to a Call.
//
// Demonstrates the feedback lifecycle:
//   POST /feedback/create  -> attach feedback to a Call
//   POST /feedback/query   -> read it back
//
// Three wire-level points worth knowing:
//
//   - The Call is identified by weave_ref, not call_id directly:
//       weave:///{entity}/{project}/call/{call_id}
//     The recipe constructs this URI inline. A call_ref field also
//     exists, but weave_ref is the required one.
//   - /feedback/create body is flat — top-level project_id, weave_ref,
//     feedback_type, payload (no wrapper key, like /call/update).
//   - /feedback/query uses the typed Query language. Filtering by
//     weave_ref is {"$expr": {"$eq": [{"$getField": "weave_ref"},
//     {"$literal": "weave:///..."}]}}.
//
// feedback_type is a freeform string. By convention wandb.<kind>.<version>
// is reserved for W&B-recognized types with UI treatment (wandb.note.1,
// wandb.reaction.1); scorer-emitted feedback uses the scorer name as a
// prefix. This recipe attaches one of each to show the many-to-one shape.
//
// Run:
//   jbang java/05_AddFeedback.java

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

class AddFeedback {
    static final ObjectMapper MAPPER = new ObjectMapper();
    static final HttpClient HTTP = HttpClient.newHttpClient();
    static final String BASE_URL = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai");
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

    public static void main(String[] args) throws Exception {
        var missing = new ArrayList<String>();
        for (String k : List.of("WANDB_API_KEY", "WANDB_ENTITY", "WANDB_PROJECT"))
            if (getenv(k, "").isEmpty()) missing.add(k);
        if (!missing.isEmpty())
            fail("Missing required env vars: " + String.join(", ", missing) + ". See ../README.md#setup.");

        projectId = System.getenv("WANDB_ENTITY") + "/" + System.getenv("WANDB_PROJECT");
        authHeader = "Basic " + Base64.getEncoder().encodeToString(
                ("api:" + System.getenv("WANDB_API_KEY")).getBytes(StandardCharsets.UTF_8));

        final String opName = "recipe-05-add-feedback";
        var attributes = new LinkedHashMap<String, Object>();
        attributes.put("cookbook.language", "java");
        attributes.put("cookbook.recipe", "05_add_feedback");
        attributes.put("cookbook.environment", getenv("COOKBOOK_ENVIRONMENT", "dev"));
        var inputs = Map.of("question", "What is the capital of Germany?");
        var output = Map.of("answer", "Berlin");

        // Two feedback items, illustrating the type-convention split.
        String humanType = "wandb.note.1";
        Map<String, Object> humanPayload = Map.of("note", "Answer looks correct.");
        String scorerType = "recipe-05-scorer-correctness";
        Map<String, Object> scorerPayload = Map.of("output", Map.of("score", 1.0, "reason", "Answer matches expected"));

        // Open the Call.
        JsonNode started = postJson("/call/start", Map.of(
                "start", Map.of(
                        "project_id", projectId,
                        "op_name", opName,
                        "started_at", Instant.now().toString(),
                        "attributes", attributes,
                        "inputs", inputs)));
        String callId = started.get("id").asText();
        System.out.println("Started: id=" + callId);

        // Close it.
        postJson("/call/end", Map.of(
                "end", Map.of(
                        "project_id", projectId,
                        "id", callId,
                        "ended_at", Instant.now().toString(),
                        "summary", Map.of(),
                        "output", output)));
        System.out.println("Ended:   id=" + callId);

        // Build the Call's weave_ref. /feedback/create takes this URI string,
        // not a raw call_id.
        String callRef = "weave:///" + projectId + "/call/" + callId;

        // Attach both feedback items.
        var feedbacks = List.of(
                Map.of("feedback_type", humanType, "payload", humanPayload),
                Map.of("feedback_type", scorerType, "payload", scorerPayload));
        for (var fb : feedbacks) {
            JsonNode res = postJson("/feedback/create", Map.of(
                    "project_id", projectId,
                    "weave_ref", callRef,
                    "feedback_type", fb.get("feedback_type"),
                    "payload", fb.get("payload")));
            System.out.println("Feedback: id=" + res.path("id").asText() + " type=" + fb.get("feedback_type"));
        }

        // --- verification ---
        // Query feedback filtered to this Call by weave_ref, asserting both items
        // land with the expected feedback_type + payload. Brief retry tolerates
        // eventual consistency in the read path.
        List<String> expectedTypes = List.of(humanType, scorerType);
        var byType = new LinkedHashMap<String, JsonNode>();
        for (int i = 0; i < 5; i++) {
            JsonNode res = postJson("/feedback/query", Map.of(
                    "project_id", projectId,
                    "query", Map.of("$expr", Map.of("$eq", List.of(
                            Map.of("$getField", "weave_ref"),
                            Map.of("$literal", callRef))))));
            byType.clear();
            for (JsonNode row : res.path("result")) {
                String t = row.path("feedback_type").asText(null);
                if (t != null) byType.put(t, row);
            }
            if (byType.keySet().containsAll(expectedTypes)) break;
            Thread.sleep(1000);
        }

        if (!byType.keySet().containsAll(expectedTypes))
            fail("feedback for " + callRef + " not all visible after 5 reads (got " + byType.keySet() + ")");

        if (!MAPPER.valueToTree(humanPayload).equals(byType.get(humanType).get("payload")))
            fail("human payload: " + byType.get(humanType).get("payload"));
        if (!MAPPER.valueToTree(scorerPayload).equals(byType.get(scorerType).get("payload")))
            fail("scorer payload: " + byType.get(scorerType).get("payload"));
        for (JsonNode row : byType.values())
            if (!callRef.equals(row.path("weave_ref").asText()))
                fail("weave_ref drift: " + row.get("weave_ref"));
        System.out.println("Verified: " + byType.size() + " feedback items on " + callRef);
    }
}
