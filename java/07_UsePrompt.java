///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

// Recipe 07: publish a Prompt + reference it from a Call + tag/alias it.
//
// Introduces four new things that recipes 08-13 build on:
//
//   POST /obj/create                       -> generic Weave Object endpoint;
//                                             here, publish a StringPrompt
//   POST /obj/read                         -> read it back
//   PUT  /objs/{id}/versions/{digest}/tags -> add version tags
//   PUT  /objs/{id}/aliases                -> set named pointers
//
// (and the existing /call/start + /call/end, but now with inputs.prompt =
// a weave:// ref to the Prompt — the "object ref in trace inputs" pattern
// that unlocks Model.predict, Scorer Ops, and the eval flow.)
//
// Wire-level points worth knowing:
//
//   - The Object endpoint is flat under an `obj` wrapper:
//     {"obj": {"project_id", "object_id", "val"}}. The val is stored
//     verbatim (after lowercasing object_id) and MUST carry _bases,
//     _class_name, and _type for the UI to recognise the object.
//   - base_object_class ("Prompt") is derived from val._bases;
//     leaf_object_class from val._class_name.
//   - Tags are per-version additive labels; aliases are per-object_id
//     named pointers. Both are UI-visible metadata separate from val, so
//     changing them does NOT bump the version. The server auto-maintains
//     a `latest` alias — do not set it yourself.
//
// Run:
//   jbang java/07_UsePrompt.java

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

class UsePrompt {
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

    // Centralizes auth + JSON (de)serialization for any method. Any non-2xx
    // is fatal. post/put wrap it for the two verbs this recipe uses.
    static JsonNode doJson(String method, String path, Object body) throws Exception {
        String json = MAPPER.writeValueAsString(body);
        HttpRequest req = HttpRequest.newBuilder(URI.create(BASE_URL + path))
                .header("Authorization", authHeader)
                .header("Content-Type", "application/json")
                .method(method, HttpRequest.BodyPublishers.ofString(json, StandardCharsets.UTF_8))
                .build();
        HttpResponse<String> res = HTTP.send(req, HttpResponse.BodyHandlers.ofString());
        if (res.statusCode() / 100 != 2)
            fail("HTTP " + res.statusCode() + " for " + method + " " + path + ": " + res.body());
        String b = res.body();
        return (b == null || b.isEmpty()) ? MAPPER.createObjectNode() : MAPPER.readTree(b);
    }

    static JsonNode post(String path, Object body) throws Exception { return doJson("POST", path, body); }
    static JsonNode put(String path, Object body) throws Exception { return doJson("PUT", path, body); }

    // Reports whether a JSON array node holds the string s.
    static boolean nodeContains(JsonNode arr, String s) {
        if (arr == null || !arr.isArray()) return false;
        for (JsonNode n : arr) if (s.equals(n.asText())) return true;
        return false;
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

        // 1) Publish a StringPrompt via the generic Object endpoint. The val
        // mirrors what the SDK produces for weave.StringPrompt(content=...).
        // Built with a LinkedHashMap because val carries a null "name" (which
        // Map.of forbids) and order is nice to preserve.
        String promptObjectId = "recipe-07-prompt-java";
        String promptContent = "Answer the question concisely: {question}";
        var promptVal = new LinkedHashMap<String, Object>();
        promptVal.put("_bases", List.of("Prompt", "Object", "BaseModel"));
        promptVal.put("_class_name", "StringPrompt");
        promptVal.put("_type", "StringPrompt");
        promptVal.put("name", null);
        promptVal.put("description", "Capital-city Q&A prompt template (java recipe 07)");
        promptVal.put("content", promptContent);

        JsonNode created = post("/obj/create", Map.of(
                "obj", Map.of(
                        "project_id", projectId,
                        "object_id", promptObjectId,
                        "val", promptVal)));
        String promptDigest = created.get("digest").asText();
        String promptRef = "weave:///" + projectId + "/object/" + promptObjectId + ":" + promptDigest;
        System.out.println("Published: " + promptObjectId + " digest=" + promptDigest.substring(0, 12) + "…");
        System.out.println("  ref: " + promptRef);

        // 2) Tag this version with the cookbook environment + language. Tags are
        // per-version, additive, UI-visible labels separate from val. PUT is
        // additive (re-runs are no-ops); removal uses POST .../tags/remove.
        List<String> tagsToAdd = List.of(getenv("COOKBOOK_ENVIRONMENT", "dev"), "java");
        put("/objs/" + promptObjectId + "/versions/" + promptDigest + "/tags", Map.of(
                "project_id", projectId,
                "tags", tagsToAdd));
        System.out.println("Tagged:    " + tagsToAdd + " -> version " + promptDigest.substring(0, 12) + "…");

        // 3) Set named aliases pointing at this version. Aliases are per-object_id
        // named pointers — re-PUTting later on another version detaches them.
        List<String> aliasesToSet = List.of("staging", "v1-candidate");
        put("/objs/" + promptObjectId + "/aliases", Map.of(
                "project_id", projectId,
                "digest", promptDigest,
                "aliases", aliasesToSet));
        System.out.println("Aliased:   " + aliasesToSet + " -> version " + promptDigest.substring(0, 12) + "…");

        // 4) Read it back (with tags + aliases) and assert everything round-trips.
        JsonNode readBack = post("/obj/read", Map.of(
                "project_id", projectId,
                "object_id", promptObjectId,
                "digest", promptDigest,
                "include_tags_and_aliases", true));
        JsonNode obj = readBack.get("obj");
        JsonNode val = obj.get("val");
        if (!"StringPrompt".equals(val.path("_class_name").asText()))
            fail("_class_name: " + val.get("_class_name"));
        if (!promptContent.equals(val.path("content").asText()))
            fail("content: " + val.get("content"));
        if (!"Prompt".equals(obj.path("base_object_class").asText()))
            fail("base_object_class: " + obj.get("base_object_class"));
        if (!"StringPrompt".equals(obj.path("leaf_object_class").asText()))
            fail("leaf_object_class: " + obj.get("leaf_object_class"));
        for (String t : tagsToAdd)
            if (!nodeContains(obj.get("tags"), t))
                fail("tag '" + t + "' missing from " + obj.get("tags"));
        for (String a : aliasesToSet)
            if (!nodeContains(obj.get("aliases"), a))
                fail("alias '" + a + "' missing from " + obj.get("aliases"));
        System.out.println("Read:      version=" + obj.path("version_index").asText()
                + " tags=" + obj.get("tags") + " aliases=" + obj.get("aliases"));

        // 5) Open a Call whose inputs.prompt is the Prompt's weave:// ref — the
        // "object ref in trace inputs" pattern. The UI follows this ref and
        // renders the prompt content inline in the call view.
        String question = "What is the capital of France?";
        JsonNode started = post("/call/start", Map.of(
                "start", Map.of(
                        "project_id", projectId,
                        "op_name", "recipe-07-prompt-in-trace",
                        "started_at", Instant.now().toString(),
                        "attributes", Map.of(
                                "cookbook.language", "java",
                                "cookbook.recipe", "07_use_prompt",
                                "cookbook.environment", getenv("COOKBOOK_ENVIRONMENT", "dev")),
                        "inputs", Map.of("prompt", promptRef, "question", question))));
        String callId = started.get("id").asText();
        String traceId = started.get("trace_id").asText();
        System.out.println("Started:   id=" + callId + " (inputs.prompt = " + promptRef + ")");

        // Client-side: substitute the question into the prompt template.
        String rendered = promptContent.replace("{question}", question);
        String answer = "Paris";

        post("/call/end", Map.of(
                "end", Map.of(
                        "project_id", projectId,
                        "id", callId,
                        "ended_at", Instant.now().toString(),
                        "summary", Map.of(),
                        "output", Map.of("rendered_prompt", rendered, "answer", answer))));
        System.out.println("Ended:     id=" + callId + " output.answer=\"" + answer + "\"");

        // --- verification ---
        // Read the Call back and assert inputs.prompt round-trips as the same
        // weave:// URI we sent. Brief retry tolerates read-after-write lag.
        JsonNode call = null;
        for (int i = 0; i < 5; i++) {
            JsonNode res = post("/call/read", Map.of("project_id", projectId, "id", callId));
            call = res.get("call");
            if (call != null && !call.isNull() && call.hasNonNull("ended_at")) break;
            Thread.sleep(1000);
        }
        if (call == null || call.isNull() || !call.hasNonNull("ended_at"))
            fail("Call " + callId + " not visible/finished after 5 reads");

        if (!promptRef.equals(call.path("inputs").path("prompt").asText()))
            fail("inputs.prompt: " + call.path("inputs").get("prompt"));
        if (!question.equals(call.path("inputs").path("question").asText()))
            fail("inputs.question: " + call.path("inputs").get("question"));
        if (!answer.equals(call.path("output").path("answer").asText()))
            fail("output.answer: " + call.path("output").get("answer"));
        if (!traceId.equals(call.get("trace_id").asText()))
            fail("trace_id: " + call.get("trace_id"));
        System.out.println("Verified:  prompt ref round-trips in call inputs");
    }
}
