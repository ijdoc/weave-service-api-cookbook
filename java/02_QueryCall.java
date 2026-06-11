///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

// Recipe 02: query Calls via /calls/stream_query.
//
// Demonstrates the workhorse read endpoint:
//   POST /calls/stream_query  -> stream NDJSON of matching Calls
//
// Sets up by creating one Call (op_name="recipe-02-query-call"), then
// queries that op_name and confirms the just-created Call appears in
// the streamed results.
//
// The endpoint returns one JSON object per line (application/jsonl). We
// parse line-by-line via BodyHandlers.ofLines() rather than buffering
// the full response, demonstrating the streaming pattern in HttpClient.
//
// Run:
//   jbang java/02_QueryCall.java

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

class QueryCall {
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

    // Centralizes auth + JSON (de)serialization; the per-call payload shape
    // stays visible at the call sites below. Any non-2xx response is fatal.
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

    // POSTs to /calls/stream_query and parses the NDJSON response line-by-line
    // (BodyHandlers.ofLines streams the body), returning the matching Call rows.
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

    public static void main(String[] args) throws Exception {
        var missing = new ArrayList<String>();
        for (String k : List.of("WANDB_API_KEY", "WANDB_ENTITY", "WANDB_PROJECT"))
            if (getenv(k, "").isEmpty()) missing.add(k);
        if (!missing.isEmpty())
            fail("Missing required env vars: " + String.join(", ", missing) + ". See ../README.md#setup.");

        projectId = System.getenv("WANDB_ENTITY") + "/" + System.getenv("WANDB_PROJECT");
        authHeader = "Basic " + Base64.getEncoder().encodeToString(
                ("api:" + System.getenv("WANDB_API_KEY")).getBytes(StandardCharsets.UTF_8));

        final String opName = "recipe-02-query-call";
        var attributes = new LinkedHashMap<String, Object>();
        attributes.put("cookbook.language", "java");
        attributes.put("cookbook.recipe", "02_query_call");
        attributes.put("cookbook.environment", getenv("COOKBOOK_ENVIRONMENT", "dev"));
        var inputs = Map.of("question", "What is the capital of Spain?");
        var output = Map.of("answer", "Madrid");

        // Setup: create + end a Call we can later query for.
        JsonNode started = postJson("/call/start", Map.of(
                "start", Map.of(
                        "project_id", projectId,
                        "op_name", opName,
                        "started_at", Instant.now().toString(),
                        "attributes", attributes,
                        "inputs", inputs)));
        String callId = started.get("id").asText();
        String traceId = started.get("trace_id").asText();
        System.out.println("Created: id=" + callId);

        postJson("/call/end", Map.of(
                "end", Map.of(
                        "project_id", projectId,
                        "id", callId,
                        "ended_at", Instant.now().toString(),
                        "summary", Map.of(),
                        "output", output)));

        // Query: stream Calls matching our op_name, newest first. Retry briefly
        // to tolerate eventual consistency on the read path.
        JsonNode found = null;
        for (int i = 0; i < 5 && found == null; i++) {
            List<JsonNode> rows = streamQuery(Map.of(
                    "project_id", projectId,
                    "filter", Map.of("op_names", List.of(opName)),
                    "sort_by", List.of(Map.of("field", "started_at", "direction", "desc")),
                    "limit", 50));
            for (JsonNode c : rows) {
                // Require ended_at populated so we don't race the write-to-read
                // propagation and read a half-finalized row.
                if (callId.equals(c.path("id").asText(null)) && c.hasNonNull("ended_at")) {
                    found = c;
                    break;
                }
            }
            if (found == null) Thread.sleep(1000);
        }

        // --- verification ---
        if (found == null)
            fail("Call " + callId + " not in stream_query results after 5 attempts");

        if (!opName.equals(found.get("op_name").asText()))
            fail("op_name mismatch: " + found.get("op_name"));
        for (var e : attributes.entrySet()) {
            JsonNode actual = found.path("attributes").get(e.getKey());
            if (actual == null || !actual.asText().equals(e.getValue()))
                fail("attribute " + e.getKey() + " mismatch: " + actual);
        }
        if (!"What is the capital of Spain?".equals(found.path("inputs").path("question").asText()))
            fail("inputs mismatch: " + found.get("inputs"));
        if (!"Madrid".equals(found.path("output").path("answer").asText()))
            fail("output mismatch: " + found.get("output"));
        if (!traceId.equals(found.get("trace_id").asText()))
            fail("trace_id mismatch: " + found.get("trace_id"));
        System.out.println("Verified: id=" + callId);
    }
}
