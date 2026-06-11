///usr/bin/env jbang "$0" "$@" ; exit $?
//JAVA 17+
//DEPS com.fasterxml.jackson.core:jackson-databind:2.18.2

// Recipe 10: create a Dataset and read its rows back.
//
// Demonstrates the v2 Dataset endpoints plus the Table read needed to
// walk the rows:
//   POST /v2/{entity}/{project}/datasets
//       -> create the Dataset, returns (object_id, digest, version_index)
//   GET  /v2/{entity}/{project}/datasets/{object_id}/versions/{digest}
//       -> read Dataset metadata, including a *reference* to its rows
//   POST /table/query
//       -> read the actual rows out of the referenced Table
//
// Wire-level points worth knowing:
//
//   - These are v2 endpoints under /v2/{entity}/{project}/datasets, not a
//     v1-style /datasets/create. Entity + project live in the URL path.
//     Read uses GET (the rest of the API is POST-only); create uses POST.
//   - A Dataset is addressed by (object_id, digest) and is content-
//     addressed — identical (name, rows) collapses to the same version.
//     The name is stamped with a per-run Unix timestamp so every run
//     exercises the write path rather than resolving to an existing object.
//   - The read response's `rows` field is a *reference string* to the
//     underlying Table, not the row data. Parse the table digest out of
//     it and call /table/query. Rows come back wrapped as {digest, val,
//     original_index?} — the actual content lives under `val`.
//
// Run:
//   jbang java/10_CreateDataset.java

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
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

class CreateDataset {
    static final ObjectMapper MAPPER = new ObjectMapper();
    static final HttpClient HTTP = HttpClient.newHttpClient();
    static final String BASE_URL = getenv("WEAVE_SERVICE_URL", "https://trace.wandb.ai");
    static final Pattern TABLE_REF = Pattern.compile("/table/([A-Za-z0-9_-]+)$");
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

    // Issues a request with auth, optionally with a JSON body, and decodes the
    // JSON response. Any non-2xx is fatal. post/get wrap it.
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
        String body2 = res.body();
        return (body2 == null || body2.isEmpty()) ? MAPPER.createObjectNode() : MAPPER.readTree(body2);
    }

    static JsonNode post(String path, Object body) throws Exception { return doReq("POST", path, body); }
    static JsonNode get(String path) throws Exception { return doReq("GET", path, null); }

    // A dataset row with insertion-ordered keys (question, then answer), so
    // Jackson serializes it the same way as the other language ports.
    static Map<String, Object> qaRow(String question, String answer) {
        var row = new java.util.LinkedHashMap<String, Object>();
        row.put("question", question);
        row.put("answer", answer);
        return row;
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

        String datasetName = "recipe-10-dataset-java-" + Instant.now().getEpochSecond();
        String datasetDescription = "Capital cities for evaluation (run at " + Instant.now() + ")";
        // Rows use LinkedHashMap (not Map.of) on purpose: Map.of has no defined
        // iteration order, so Jackson could emit answer before question — a
        // different byte-shape than the other ports. Since the rows Table is
        // content-addressed by row bytes, a divergent shape pollutes the shared
        // Table with duplicate rows. LinkedHashMap preserves question-then-answer.
        List<Map<String, Object>> datasetRows = List.of(
                qaRow("What is the capital of France?", "Paris"),
                qaRow("What is the capital of Spain?", "Madrid"),
                qaRow("What is the capital of Italy?", "Rome"));

        // Create the Dataset. v2 path; entity + project go into the URL.
        JsonNode created = post("/v2/" + entity + "/" + project + "/datasets", Map.of(
                "name", datasetName,
                "description", datasetDescription,
                "rows", datasetRows));
        String objectId = created.get("object_id").asText();
        String digest = created.get("digest").asText();
        System.out.println("Created: object_id=" + objectId + " digest=" + digest.substring(0, 12)
                + "… version=" + created.path("version_index").asText());

        // Read Dataset metadata back. GET, with object_id + digest in the URL.
        JsonNode dataset = get("/v2/" + entity + "/" + project + "/datasets/" + objectId + "/versions/" + digest);
        if (!datasetName.equals(dataset.path("name").asText()))
            fail("name: " + dataset.get("name"));
        if (!datasetDescription.equals(dataset.path("description").asText()))
            fail("description: " + dataset.get("description"));
        if (!objectId.equals(dataset.path("object_id").asText()))
            fail("object_id drift: " + dataset.get("object_id"));
        if (!digest.equals(dataset.path("digest").asText()))
            fail("digest drift: " + dataset.get("digest"));
        String rowsRef = dataset.path("rows").asText();
        System.out.println("Read:    name=\"" + dataset.path("name").asText() + "\" rows_ref=\"" + rowsRef + "\"");

        // The rows field is a reference to a Table (weave:///.../table/{digest}).
        // Parse the table digest so we can /table/query it; tolerate a bare digest.
        Matcher m = TABLE_REF.matcher(rowsRef);
        String tableDigest = m.find() ? m.group(1) : rowsRef;
        System.out.println("Table digest: " + tableDigest.substring(0, Math.min(12, tableDigest.length())) + "…");

        // Query the actual rows.
        JsonNode queried = post("/table/query", Map.of("project_id", projectId, "digest", tableDigest));
        JsonNode rows = queried.path("rows");

        // --- verification ---
        // Row count + per-row content (under `val`) must match what we wrote.
        if (rows.size() != datasetRows.size())
            fail("row count: " + rows.size() + " vs " + datasetRows.size());
        for (int i = 0; i < datasetRows.size(); i++) {
            if (!MAPPER.valueToTree(datasetRows.get(i)).equals(rows.get(i).get("val")))
                fail("row " + i + " val: " + rows.get(i).get("val") + " vs " + datasetRows.get(i));
        }
        System.out.println("Verified: " + rows.size() + " rows match (first: " + rows.get(0).get("val") + ")");
    }
}
