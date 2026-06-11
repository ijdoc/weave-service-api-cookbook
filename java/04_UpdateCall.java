///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

// Recipe 04: update a Call's display_name after it finishes.
//
// Demonstrates the only mutation the service API exposes on a finished
// Call:
//   POST /call/update  -> change display_name
//
// Two wire-level quirks worth noting:
//
//   - The body is flat: top-level project_id, call_id, display_name.
//     /call/start and /call/end wrap their bodies under start / end;
//     /call/update does not. Sending {"update": {...}} will 422.
//   - The id field is named call_id, not id (which is what /call/end uses).
//
// display_name is the only user-modifiable field; attributes, inputs,
// output, etc. are immutable after /call/start.
//
// Run:
//   jbang java/04_UpdateCall.java

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

class UpdateCall {
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

        final String opName = "recipe-04-update-call";
        var attributes = new LinkedHashMap<String, Object>();
        attributes.put("cookbook.language", "java");
        attributes.put("cookbook.recipe", "04_update_call");
        attributes.put("cookbook.environment", getenv("COOKBOOK_ENVIRONMENT", "dev"));
        var inputs = Map.of("question", "What is the capital of Italy?");
        var output = Map.of("answer", "Rome");
        final String newDisplayName = "recipe 04 — updated after finish";

        // Open the Call.
        JsonNode started = postJson("/call/start", Map.of(
                "start", Map.of(
                        "project_id", projectId,
                        "op_name", opName,
                        "started_at", Instant.now().toString(),
                        "attributes", attributes,
                        "inputs", inputs)));
        String callId = started.get("id").asText();
        String traceId = started.get("trace_id").asText();
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

        // Mutate display_name. Flat body, `call_id` (not `id`), no wrapper key.
        postJson("/call/update", Map.of(
                "project_id", projectId,
                "call_id", callId,
                "display_name", newDisplayName));
        System.out.println("Updated: id=" + callId + " display_name=\"" + newDisplayName + "\"");

        // --- verification ---
        // Read the Call back and assert display_name reflects the update.
        // Brief retry loop tolerates eventual consistency in the read path.
        JsonNode call = null;
        for (int i = 0; i < 5; i++) {
            JsonNode read = postJson("/call/read", Map.of("project_id", projectId, "id", callId));
            call = read.get("call");
            if (call != null && !call.isNull() && newDisplayName.equals(call.path("display_name").asText(null)))
                break;
            Thread.sleep(1000);
        }
        if (call == null || call.isNull() || !newDisplayName.equals(call.path("display_name").asText(null)))
            fail("Call " + callId + " display_name not updated after 5 reads");

        // op_name and the rest must NOT have changed — /call/update only touches display_name.
        if (!opName.equals(call.get("op_name").asText()))
            fail("op_name drifted: " + call.get("op_name"));
        for (var e : attributes.entrySet()) {
            JsonNode actual = call.path("attributes").get(e.getKey());
            if (actual == null || !actual.asText().equals(e.getValue()))
                fail("attribute " + e.getKey() + " mismatch: " + actual);
        }
        if (!"What is the capital of Italy?".equals(call.path("inputs").path("question").asText()))
            fail("inputs mismatch: " + call.get("inputs"));
        if (!"Rome".equals(call.path("output").path("answer").asText()))
            fail("output mismatch: " + call.get("output"));
        if (!traceId.equals(call.get("trace_id").asText()))
            fail("trace_id mismatch: " + call.get("trace_id"));
        System.out.println("Verified: id=" + callId + " display_name=\"" + call.path("display_name").asText() + "\"");
    }
}
