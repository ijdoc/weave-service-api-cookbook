///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

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
//   jbang java/03_ParentChildCalls.java

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
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;
import java.util.stream.Stream;

class ParentChildCalls {
    static final ObjectMapper MAPPER = new ObjectMapper();
    static final HttpClient HTTP = HttpClient.newHttpClient();
    static final String BASE_URL = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai");
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

    // Parses the NDJSON /calls/stream_query response line-by-line.
    static List<JsonNode> streamQuery(Object body) throws Exception {
        String json = MAPPER.writeValueAsString(body);
        HttpRequest req = HttpRequest.newBuilder(URI.create(BASE_URL + "/calls/stream_query"))
                .header("Authorization", authHeader)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(json, StandardCharsets.UTF_8))
                .build();
        HttpResponse<Stream<String>> res = HTTP.send(req, HttpResponse.BodyHandlers.ofLines());
        if (res.statusCode() / 100 != 2)
            fail("HTTP " + res.statusCode() + " for /calls/stream_query: "
                    + res.body().collect(Collectors.joining("\n")));
        var rows = new ArrayList<JsonNode>();
        for (Iterator<String> it = res.body().iterator(); it.hasNext(); ) {
            String line = it.next();
            if (line.isBlank()) continue;
            rows.add(MAPPER.readTree(line));
        }
        return rows;
    }

    // POSTs /call/start. parentId and traceId are omitted when null, so a
    // top-level Call passes null for both and the server assigns a trace_id.
    static JsonNode startCall(String opName, Map<String, Object> inputs, String parentId, String traceId)
            throws Exception {
        var start = new LinkedHashMap<String, Object>();
        start.put("project_id", projectId);
        start.put("op_name", opName);
        start.put("started_at", Instant.now().toString());
        start.put("attributes", baseAttributes);
        start.put("inputs", inputs);
        if (parentId != null) start.put("parent_id", parentId);
        if (traceId != null) start.put("trace_id", traceId);
        return postJson("/call/start", Map.of("start", start));
    }

    // POSTs /call/end.
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
        baseAttributes.put("cookbook.recipe", "03_parent_child_calls");
        baseAttributes.put("cookbook.environment", getenv("COOKBOOK_ENVIRONMENT", "dev"));

        String question = "Where is the Eiffel Tower?";
        List<String> docs = List.of("Paris", "France");
        String answer = "In Paris, France.";

        // Open the parent (top-level: no parent_id, no explicit trace_id).
        // The server assigns a trace_id which we propagate to children.
        JsonNode parent = startCall("recipe-03-rag-pipeline", Map.of("question", question), null, null);
        String parentId = parent.get("id").asText();
        String traceId = parent.get("trace_id").asText();
        System.out.println("Started parent: id=" + parentId + " trace_id=" + traceId);

        // Open + finish the first child (retrieve), under the parent + trace.
        JsonNode retrieve = startCall("recipe-03-retrieve", Map.of("question", question), parentId, traceId);
        String retrieveId = retrieve.get("id").asText();
        System.out.println("Started child 1: id=" + retrieveId);
        endCall(retrieveId, Map.of("docs", docs));
        System.out.println("Ended   child 1: id=" + retrieveId);

        // Open + finish the second child (generate).
        JsonNode generate = startCall("recipe-03-generate", Map.of("docs", docs, "question", question), parentId, traceId);
        String generateId = generate.get("id").asText();
        System.out.println("Started child 2: id=" + generateId);
        endCall(generateId, Map.of("answer", answer));
        System.out.println("Ended   child 2: id=" + generateId);

        // Close the parent (after all children have finished).
        endCall(parentId, Map.of("answer", answer));
        System.out.println("Ended   parent:  id=" + parentId);

        // --- verification ---
        // Stream all Calls in this trace; assert parent + 2 children, with
        // parent.parent_id absent and children.parent_id = parent_id.
        List<String> expected = List.of(parentId, retrieveId, generateId);
        var found = new LinkedHashMap<String, JsonNode>();
        for (int i = 0; i < 5; i++) {
            List<JsonNode> rows = streamQuery(Map.of(
                    "project_id", projectId,
                    "filter", Map.of("trace_ids", List.of(traceId))));
            found.clear();
            for (JsonNode c : rows) {
                String id = c.path("id").asText(null);
                if (id != null) found.put(id, c);
            }
            // Require all three visible AND finalized (ended_at populated) so we
            // don't race write-to-read propagation on inner-field reads.
            boolean ready = expected.stream().allMatch(id -> found.containsKey(id) && found.get(id).hasNonNull("ended_at"));
            if (ready) break;
            Thread.sleep(1000);
        }

        for (String id : expected)
            if (!found.containsKey(id))
                fail("trace " + traceId + " missing call " + id);

        JsonNode parentCall = found.get(parentId), retrieveCall = found.get(retrieveId), generateCall = found.get(generateId);
        if (parentCall.hasNonNull("parent_id"))
            fail("parent has parent_id: " + parentCall.get("parent_id"));
        if (!parentId.equals(retrieveCall.path("parent_id").asText(null)))
            fail("retrieve.parent_id: " + retrieveCall.get("parent_id"));
        if (!parentId.equals(generateCall.path("parent_id").asText(null)))
            fail("generate.parent_id: " + generateCall.get("parent_id"));
        for (JsonNode c : List.of(parentCall, retrieveCall, generateCall)) {
            if (!traceId.equals(c.get("trace_id").asText()))
                fail("trace_id on " + c.get("id") + ": " + c.get("trace_id"));
            for (var e : baseAttributes.entrySet()) {
                JsonNode actual = c.path("attributes").get(e.getKey());
                if (actual == null || !actual.asText().equals(e.getValue()))
                    fail("attribute " + e.getKey() + " on " + c.get("id") + ": " + actual);
            }
        }
        System.out.println("Verified: trace_id=" + traceId + " (1 parent + 2 children)");
    }
}
