///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

// Recipe 11: set up an Evaluation Object.
//
// Pulls everything from earlier recipes together into a single Evaluation
// definition — the versioned, content-addressed Object that recipe 12
// executes and recipe 13 queries against. After this runs, the W&B UI's
// Evaluation Definitions page shows it as a definition with no runs yet.
//
// The recipe builds two kinds of artifacts:
//
//  1. Three canonical Eval Ops (Evaluation.evaluate,
//     Evaluation.predict_and_score, Evaluation.summarize) — inert
//     lifecycle-marker Ops registered via the two-step /file/create +
//     /obj/create flow with ADR-0004 scaffolds. The service identifies
//     these by object_id (case-sensitive — /eval_results/query filters
//     on the exact canonical names), so the object_ids stay SHARED
//     across languages (no -java suffix), unlike Model/Scorer/Dataset.
//  2. The Evaluation Object itself — POST /obj/create with
//     builtin_object_class="Evaluation", referencing the canonical Ops +
//     the recipe-08 Model + recipe-09 Scorer Op + recipe-10 Dataset.
//
// The canonical Op scaffolds live ONLY here (not in recipes 12/13) so
// editing the eval's definition is a single-file change. /file/create is
// the ONE multipart endpoint the cookbook uses; everything else is JSON.
//
// Run:
//   jbang java/11_CreateEvaluation.java

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;

import java.io.ByteArrayOutputStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

class CreateEvaluation {
    static final ObjectMapper MAPPER = new ObjectMapper();
    static final HttpClient HTTP = HttpClient.newHttpClient();
    static final String BASE_URL = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai");
    static final String RECIPE_PATH = "java/11_CreateEvaluation.java";
    static String entity;
    static String project;
    static String projectId;
    static String authHeader;
    static String recipeSha;

    static String getenv(String key, String def) {
        String v = System.getenv(key);
        return (v == null || v.isEmpty()) ? def : v;
    }

    static void fail(String msg) {
        System.err.println("FAIL: " + msg);
        System.exit(1);
    }

    // Auth'd request (optional JSON body) decoded as JSON. Non-2xx is fatal.
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
    static JsonNode put(String path, Object body) throws Exception { return doReq("PUT", path, body); }

    // Uploads Op source as a multipart file and returns the file digest (goes
    // into the Op's val under files.obj.py). HttpClient has no multipart
    // helper, so we assemble the body by hand. /file/create is the only
    // multipart endpoint the cookbook uses.
    static String uploadOpSource(String source) throws Exception {
        String boundary = "----weaveBoundary" + Long.toHexString(System.nanoTime());
        String crlf = "\r\n";
        String head = "--" + boundary + crlf
                + "Content-Disposition: form-data; name=\"project_id\"" + crlf + crlf
                + projectId + crlf
                + "--" + boundary + crlf
                + "Content-Disposition: form-data; name=\"file\"; filename=\"obj.py\"" + crlf
                + "Content-Type: application/octet-stream" + crlf + crlf;
        var body = new ByteArrayOutputStream();
        body.write(head.getBytes(StandardCharsets.UTF_8));
        body.write(source.getBytes(StandardCharsets.UTF_8));
        body.write((crlf + "--" + boundary + "--" + crlf).getBytes(StandardCharsets.UTF_8));
        HttpRequest req = HttpRequest.newBuilder(URI.create(BASE_URL + "/file/create"))
                .header("Authorization", authHeader)
                .header("Content-Type", "multipart/form-data; boundary=" + boundary)
                .POST(HttpRequest.BodyPublishers.ofByteArray(body.toByteArray()))
                .build();
        HttpResponse<String> res = HTTP.send(req, HttpResponse.BodyHandlers.ofString());
        if (res.statusCode() / 100 != 2)
            fail("HTTP " + res.statusCode() + " for /file/create: " + res.body());
        return MAPPER.readTree(res.body()).get("digest").asText();
    }

    // Latest version of object_id, or null if absent.
    static JsonNode latestObject(String objectId) throws Exception {
        JsonNode r = post("/objs/query", Map.of(
                "project_id", projectId,
                "filter", Map.of("object_ids", List.of(objectId), "latest_only", true),
                "metadata_only", true));
        JsonNode objs = r.path("objs");
        return objs.isArray() && objs.size() > 0 ? objs.get(0) : null;
    }

    // Most-recently-created Dataset whose object_id starts with prefix
    // (recipe 10 timestamps Dataset names, so exact lookup won't work).
    static JsonNode latestDatasetByPrefix(String prefix) throws Exception {
        JsonNode r = post("/objs/query", Map.of(
                "project_id", projectId,
                "filter", Map.of("base_object_classes", List.of("Dataset")),
                "sort_by", List.of(Map.of("field", "created_at", "direction", "desc")),
                "limit", 50,
                "metadata_only", true));
        for (JsonNode o : r.path("objs"))
            if (o.path("object_id").asText().startsWith(prefix)) return o;
        return null;
    }

    static String objRef(JsonNode o) {
        return "weave:///" + projectId + "/object/" + o.get("object_id").asText() + ":" + o.get("digest").asText();
    }

    static String opRef(JsonNode o) {
        return "weave:///" + projectId + "/op/" + o.get("object_id").asText() + ":" + o.get("digest").asText();
    }

    // ADR-0004 Python scaffold for a canonical Eval Op. The body is inert;
    // the service identifies the Op by object_id, not behaviour.
    static String scaffold(String opName, String signature, String bodyDoc) {
        return """
                # Cookbook scaffold (java)
                # Source: %s
                # SHA256: %s

                import weave


                @weave.op
                def %s:
                    \"\"\"%s

                    Byte-for-byte reference (SHA256 of the recipe file):
                        %s

                    To verify a local copy of the file matches (POSIX shell):
                        shasum -a 256 %s | cut -c1-16

                    Canonical lifecycle-marker Op for the cookbook's eval flow. The
                    W&B service identifies this Op by `object_id` ('%s') and uses it
                    to recognise the structured Call trace recipe 12 builds. The body
                    raises NotImplementedError by design — real eval logic lives
                    client-side in recipe 12.
                    \"\"\"
                    raise NotImplementedError(
                        "This op is a Python scaffold uploaded from a non-Python recipe. "
                        "See the docstring above for the real source-language file and a "
                        "verifiable byte-for-byte reference (SHA256)."
                    )
                """.formatted(RECIPE_PATH, recipeSha, signature, bodyDoc, recipeSha, RECIPE_PATH, opName);
    }

    static boolean containsAll(JsonNode arr, List<String> want) {
        var have = new java.util.HashSet<String>();
        if (arr != null && arr.isArray())
            for (JsonNode n : arr) have.add(n.asText());
        return have.containsAll(want);
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
        byte[] recipeBytes = Files.readAllBytes(Path.of(RECIPE_PATH));
        byte[] hash = MessageDigest.getInstance("SHA-256").digest(recipeBytes);
        StringBuilder hex = new StringBuilder();
        for (byte b : hash) hex.append(String.format("%02x", b));
        recipeSha = hex.substring(0, 16);

        // 1) Look up the prerequisites from earlier recipes.
        JsonNode model = latestObject("recipe-08-model-java");
        if (model == null) fail("model `recipe-08-model-java` not found. Run java/08_UseModel.java first.");
        System.out.println("Found:     model    " + model.get("object_id").asText() + " digest=" + model.get("digest").asText().substring(0, 12) + "…");

        JsonNode scorer = latestObject("recipe-09-is-correct-java");
        if (scorer == null) fail("scorer `recipe-09-is-correct-java` not found. Run java/09_ScoreACall.java first.");
        System.out.println("Found:     scorer   " + scorer.get("object_id").asText() + " digest=" + scorer.get("digest").asText().substring(0, 12) + "…");

        JsonNode dataset = latestDatasetByPrefix("recipe-10-dataset-java");
        if (dataset == null) fail("no Dataset matching `recipe-10-dataset-java-*` found. Run java/10_CreateDataset.java first.");
        System.out.println("Found:     dataset  " + dataset.get("object_id").asText() + " digest=" + dataset.get("digest").asText().substring(0, 12) + "…");

        // 2) Register the three canonical Eval Ops with ADR-0004 scaffolds.
        record CanonOp(String id, String signature, String bodyDoc, String field) {}
        List<CanonOp> canonicalOps = List.of(
                new CanonOp("Evaluation.evaluate", "evaluate(self, model)",
                        "Root of an evaluation Call trace. Wraps one full pass over\n        the dataset with the given model + scorers.", "evaluate"),
                new CanonOp("Evaluation.predict_and_score", "predict_and_score(self, example)",
                        "Per-row child of the eval root. One trial = one dataset row\n        scored by all configured scorers.", "predict_and_score"),
                new CanonOp("Evaluation.summarize", "summarize(self, eval_table)",
                        "Final sibling of predict_and_score children under the root.\n        Aggregates per-row scorer outputs into evaluation-level stats.", "summarize"));
        var evalOpRefs = new LinkedHashMap<String, String>();
        for (CanonOp op : canonicalOps) {
            String fileDigest = uploadOpSource(scaffold(op.id(), op.signature(), op.bodyDoc()));
            JsonNode res = post("/obj/create", Map.of(
                    "obj", Map.of(
                            "project_id", projectId,
                            "object_id", op.id(),
                            "val", Map.of(
                                    "_type", "CustomWeaveType",
                                    "files", Map.of("obj.py", fileDigest),
                                    "weave_type", Map.of("type", "Op")))));
            evalOpRefs.put(op.id(), "weave:///" + projectId + "/op/" + res.get("object_id").asText() + ":" + res.get("digest").asText());
            System.out.println("Op:        " + res.get("object_id").asText() + " digest=" + res.get("digest").asText().substring(0, 12)
                    + "… (file=" + fileDigest.substring(0, 12) + "…)");
        }

        // 3) Build the Evaluation Object (val mirrors the SDK shape).
        String evalObjectId = "recipe-11-eval-java";
        var evalVal = new LinkedHashMap<String, Object>();
        evalVal.put("_bases", List.of("Object", "BaseModel"));
        evalVal.put("_class_name", "Evaluation");
        evalVal.put("_type", "Evaluation");
        evalVal.put("name", evalObjectId);
        evalVal.put("description", "Cookbook evaluation definition (java recipe 11)");
        evalVal.put("dataset", objRef(dataset));
        evalVal.put("evaluate", evalOpRefs.get("Evaluation.evaluate"));
        evalVal.put("predict_and_score", evalOpRefs.get("Evaluation.predict_and_score"));
        evalVal.put("summarize", evalOpRefs.get("Evaluation.summarize"));
        evalVal.put("scorers", List.of(opRef(scorer)));
        evalVal.put("trials", 1);
        evalVal.put("evaluation_name", null);
        evalVal.put("metadata", null);
        evalVal.put("preprocess_model_input", null);
        JsonNode created = post("/obj/create", Map.of(
                "obj", Map.of(
                        "project_id", projectId,
                        "object_id", evalObjectId,
                        "val", evalVal,
                        "builtin_object_class", "Evaluation")));
        String evalDigest = created.get("digest").asText();
        String evalRef = "weave:///" + projectId + "/object/" + evalObjectId + ":" + evalDigest;
        System.out.println("Published: " + evalObjectId + " digest=" + evalDigest.substring(0, 12) + "…");
        System.out.println("  ref: " + evalRef);

        // 4) Tag + alias (recipe 07's pattern).
        List<String> tagsToAdd = List.of(getenv("COOKBOOK_ENVIRONMENT", "dev"), "java");
        put("/objs/" + evalObjectId + "/versions/" + evalDigest + "/tags", Map.of("project_id", projectId, "tags", tagsToAdd));
        System.out.println("Tagged:    " + tagsToAdd + " -> version " + evalDigest.substring(0, 12) + "…");
        List<String> aliasesToSet = List.of("staging");
        put("/objs/" + evalObjectId + "/aliases", Map.of("project_id", projectId, "digest", evalDigest, "aliases", aliasesToSet));
        System.out.println("Aliased:   " + aliasesToSet + " -> version " + evalDigest.substring(0, 12) + "…");

        // --- verification ---
        // Read the Eval Object back (with tags + aliases) and assert every ref
        // + metadata field round-trips. Retry until tags + aliases propagate.
        JsonNode obj = null;
        for (int i = 0; i < 8; i++) {
            JsonNode r = post("/obj/read", Map.of(
                    "project_id", projectId,
                    "object_id", evalObjectId,
                    "digest", evalDigest,
                    "include_tags_and_aliases", true));
            obj = r.get("obj");
            if (obj != null && !obj.isNull()
                    && containsAll(obj.get("tags"), tagsToAdd) && containsAll(obj.get("aliases"), aliasesToSet))
                break;
            Thread.sleep(1000);
        }
        if (obj == null || obj.isNull())
            fail("Eval Object " + evalObjectId + ":" + evalDigest + " not visible after 8 reads");

        JsonNode val = obj.get("val");
        if (!"Evaluation".equals(val.path("_class_name").asText())) fail("_class_name: " + val.get("_class_name"));
        if (!objRef(dataset).equals(val.path("dataset").asText())) fail("dataset: " + val.get("dataset"));
        for (CanonOp op : canonicalOps)
            if (!evalOpRefs.get(op.id()).equals(val.path(op.field()).asText()))
                fail(op.field() + ": " + val.get(op.field()));
        JsonNode scorers = val.path("scorers");
        if (scorers.size() != 1 || !opRef(scorer).equals(scorers.get(0).asText())) fail("scorers: " + scorers);
        if (!"Evaluation".equals(obj.path("base_object_class").asText())) fail("base_object_class: " + obj.get("base_object_class"));
        if (!containsAll(obj.get("tags"), tagsToAdd)) fail("tags: " + obj.get("tags"));
        if (!containsAll(obj.get("aliases"), aliasesToSet)) fail("aliases: " + obj.get("aliases"));
        System.out.println("Verified:  Eval Object refs + tags + aliases round-trip (tags=" + obj.get("tags") + ", aliases=" + obj.get("aliases") + ")");
    }
}
