///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

// Recipe 08: create a versioned Model + use it in a trace.
//
// First application of ADR-0004 (the source-embedding scaffold). The
// recipe creates two Weave Objects:
//
//   POST /v2/{entity}/{project}/ops   -> register the predict Op
//                                        (Python scaffold per ADR-0004)
//   POST /obj/create                  -> register the Model object,
//                                        pointing val.predict at the
//                                        predict Op's weave:// ref
//
// Then it opens a Call that references both — establishing the "predict
// logic lives in the recipe file; Weave records identity + invocation"
// pattern that recipes 09-12 reuse.
//
// Wire-level points worth knowing:
//
//   - The Model is created via /obj/create, NOT /v2/.../models. The
//     generic Object endpoint takes structured metadata (a predict field
//     pointing at the Op ref) that makes the UI render predict inline.
//   - The Model val mirrors the SDK shape: _bases=["Model","Object",
//     "BaseModel"], _class_name/_type a real subclass name, a predict
//     weave:// ref, plus instance attributes (model_name, temperature,
//     max_tokens) that distinguish one Model version from another.
//     Per-Call data (the question, the answer) lives on the Call.
//   - The UI's CallPage parses op_name and inputs.self as weave:// URIs
//     and crashes on raw strings — both MUST be real refs.
//
// Editing this file changes its SHA256 -> the Op scaffold changes ->
// Weave bumps the predict Op's version_index. Per-language identity comes
// from the Model + Op object_ids (recipe-08-model-java[.predict]).
//
// For brevity this recipe mocks the LLM invocation — the Call's output is
// a hardcoded answer.
//
// Run:
//   jbang java/08_UseModel.java

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
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

class UseModel {
    static final ObjectMapper MAPPER = new ObjectMapper();
    static final HttpClient HTTP = HttpClient.newHttpClient();
    static final String BASE_URL = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai");
    static final String RECIPE_PATH = "java/08_UseModel.java";
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

        // --- ADR-0004 scaffold for the predict Op ---
        // SHA256 of this recipe file's bytes (read relative to the repo root,
        // the documented run CWD). Edits flow through to opSource, which is
        // what Weave content-addresses on; re-running unchanged is idempotent.
        byte[] recipeBytes = Files.readAllBytes(Path.of(RECIPE_PATH));
        byte[] hash = MessageDigest.getInstance("SHA-256").digest(recipeBytes);
        StringBuilder hex = new StringBuilder();
        for (byte b : hash) hex.append(String.format("%02x", b));
        String recipeSha = hex.substring(0, 16);

        String opSource = """
                # Cookbook scaffold (java)
                # Source: %s
                # SHA256: %s

                import weave


                @weave.op
                def predict(self, question):
                    \"\"\"The actual predict implementation lives in:
                        %s

                    Byte-for-byte reference (SHA256 of the recipe file):
                        %s

                    To verify a local copy of the file matches (POSIX shell):
                        shasum -a 256 %s | cut -c1-16

                    This Python op is a metadata handle, not the real model — running
                    it raises NotImplementedError by design.
                    \"\"\"
                    raise NotImplementedError(
                        "This op is a Python scaffold uploaded from a non-Python recipe. "
                        "See the docstring above for the real source-language file and a "
                        "verifiable byte-for-byte reference (SHA256)."
                    )
                """.formatted(RECIPE_PATH, recipeSha, RECIPE_PATH, recipeSha, RECIPE_PATH);

        // 1) Register the predict Op via the specialized /v2/.../ops endpoint.
        // Object_id is <ClassName>.predict by convention; the server lowercases
        // it. The Op carries the ADR-0004 scaffold as its source.
        String opName = "recipe-08-model-java.predict";
        JsonNode opRes = post("/v2/" + entity + "/" + project + "/ops", Map.of(
                "name", opName,
                "source_code", opSource));
        String predictOpRef = "weave:///" + projectId + "/op/" + opRes.get("object_id").asText() + ":" + opRes.get("digest").asText();
        System.out.println("Predict op: " + opRes.get("object_id").asText()
                + " digest=" + opRes.get("digest").asText().substring(0, 12)
                + "… version=" + opRes.path("version_index").asText());

        // 2) Register the Model via the generic /obj/create endpoint. The val
        // mirrors the SDK's Model shape; instance attributes are the kind of
        // config a real Model carries — change any value and you get a new
        // (digest, version_index).
        String modelObjectId = "recipe-08-model-java";
        var modelVal = new LinkedHashMap<String, Object>();
        modelVal.put("_bases", List.of("Model", "Object", "BaseModel"));
        modelVal.put("_class_name", "Recipe08JavaModel");
        modelVal.put("_type", "Recipe08JavaModel");
        modelVal.put("name", modelObjectId);
        modelVal.put("description", "Cookbook model handle (java recipe 08)");
        modelVal.put("model_name", "gpt-4o-mini");
        modelVal.put("temperature", 0.7);
        modelVal.put("max_tokens", 100);
        modelVal.put("predict", predictOpRef);
        JsonNode modelRes = post("/obj/create", Map.of(
                "obj", Map.of(
                        "project_id", projectId,
                        "object_id", modelObjectId,
                        "val", modelVal)));
        String modelDigest = modelRes.get("digest").asText();
        String modelRef = "weave:///" + projectId + "/object/" + modelObjectId + ":" + modelDigest;
        System.out.println("Model:      " + modelRes.get("object_id").asText() + " digest=" + modelDigest.substring(0, 12) + "…");
        System.out.println("  ref: " + modelRef);

        // 3) Open a Call that uses the predict Op + Model. op_name MUST be the
        // Op ref (not a bare string), and inputs.self MUST be the Model ref.
        String question = "Is the sky blue?";
        JsonNode started = post("/call/start", Map.of(
                "start", Map.of(
                        "project_id", projectId,
                        "op_name", predictOpRef,
                        "started_at", Instant.now().toString(),
                        "attributes", Map.of(
                                "cookbook.language", "java",
                                "cookbook.recipe", "08_use_model",
                                "cookbook.environment", getenv("COOKBOOK_ENVIRONMENT", "dev")),
                        "inputs", Map.of("self", modelRef, "question", question))));
        String callId = started.get("id").asText();
        String traceId = started.get("trace_id").asText();
        System.out.println("Started:    id=" + callId);

        // 4) End the Call with the model's answer. A real recipe would call the
        // LLM named in model_name here; we hardcode an answer to stay focused
        // on the wire-level Model + Op + Call wiring.
        String answer = "yes";
        post("/call/end", Map.of(
                "end", Map.of(
                        "project_id", projectId,
                        "id", callId,
                        "ended_at", Instant.now().toString(),
                        "summary", Map.of(
                                "status_counts", Map.of("success", 1, "error", 0),
                                "weave", Map.of("status", "success", "trace_name", opName)),
                        "output", answer)));
        System.out.println("Ended:      id=" + callId + " output=\"" + answer + "\"");

        // --- verification ---
        // Read the Call back and assert the model + op linkage round-trips.
        JsonNode call = null;
        for (int i = 0; i < 5; i++) {
            JsonNode res = post("/call/read", Map.of("project_id", projectId, "id", callId));
            call = res.get("call");
            if (call != null && !call.isNull() && call.hasNonNull("ended_at")) break;
            Thread.sleep(1000);
        }
        if (call == null || call.isNull() || !call.hasNonNull("ended_at"))
            fail("Call " + callId + " not visible/finished after 5 reads");

        if (!predictOpRef.equals(call.path("op_name").asText()))
            fail("op_name: " + call.get("op_name"));
        if (!modelRef.equals(call.path("inputs").path("self").asText()))
            fail("inputs.self: " + call.path("inputs").get("self"));
        if (!question.equals(call.path("inputs").path("question").asText()))
            fail("inputs.question: " + call.path("inputs").get("question"));
        if (!answer.equals(call.path("output").asText()))
            fail("output: " + call.get("output"));
        if (!traceId.equals(call.get("trace_id").asText()))
            fail("trace_id: " + call.get("trace_id"));
        System.out.println("Verified:   id=" + callId + " (op + model + output round-tripped)");
    }
}
