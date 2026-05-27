// Recipe 07: create a Dataset and read its rows back.
//
// Demonstrates the v2 Dataset endpoints plus the Table read needed to
// walk the rows:
//   POST   /v2/{entity}/{project}/datasets
//       -> create the Dataset, returns (object_id, digest, version_index)
//   GET    /v2/{entity}/{project}/datasets/{object_id}/versions/{digest}
//       -> read Dataset metadata, including a *reference* to its rows
//   POST   /table/query
//       -> read the actual rows out of the referenced Table
//
// Three wire-level points worth knowing:
//
// - These are the v2 endpoints under `/v2/{entity}/{project}/datasets`,
//   not a v1-style `POST /datasets/create`. Entity and project live in
//   the URL path rather than in the request body. Read uses GET (the
//   rest of the service API is POST-only); create uses POST with a JSON
//   body.
// - A Dataset is addressed by `(object_id, digest)`. `object_id` is
//   stable across versions; `digest` pins a specific version. Datasets
//   with the same `name` accumulate as new versions of one logical
//   Dataset. Datasets are *content-addressed* — identical (name, rows)
//   collapses to the same (digest, version_index). To make sure the
//   recipe actually exercises the write path on every run (rather than
//   silently resolving to an existing object), the dataset name is
//   stamped with a per-run Unix-epoch timestamp.
// - The Dataset read response's `rows` field is a *reference string* to
//   the underlying Table, not the row data. To walk rows, parse the
//   table digest out of that reference and call `/table/query`. Rows are
//   wrapped as {digest, val, original_index?} — the actual row content
//   lives under `val`.
//
// Run:
//   dotnet run dotnet/07_CreateDataset.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// .NET 10 file-based programs disable reflection-based JSON serialization
// by default; this resolver re-enables it. See 01_StartCall.cs for context.
JsonSerializerOptions jsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

string baseUrl = Environment.GetEnvironmentVariable("WEAVE_SERVICE_URL") ?? "https://trace.wandb.ai";

string[] required = { "WANDB_API_KEY", "WANDB_ENTITY", "WANDB_PROJECT" };
var missing = new List<string>();
foreach (var k in required)
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k))) missing.Add(k);
if (missing.Count > 0)
{
    Console.Error.WriteLine($"Missing required env vars: {string.Join(", ", missing)}. See ../README.md#setup.");
    return 1;
}

string apiKey = Environment.GetEnvironmentVariable("WANDB_API_KEY")!;
string entity = Environment.GetEnvironmentVariable("WANDB_ENTITY")!;
string project = Environment.GetEnvironmentVariable("WANDB_PROJECT")!;
string projectId = $"{entity}/{project}";

using var http = new HttpClient();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Basic",
    Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{apiKey}")));

async Task<JsonNode> PostJson(string path, object body)
{
    var json = JsonSerializer.Serialize(body, jsonOptions);
    var res = await http.PostAsync(baseUrl + path, new StringContent(json, Encoding.UTF8, "application/json"));
    var responseBody = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        throw new HttpRequestException($"HTTP {(int)res.StatusCode} for {path}: {responseBody}");
    return string.IsNullOrEmpty(responseBody) ? new JsonObject() : JsonNode.Parse(responseBody)!;
}

async Task<JsonNode> GetJson(string path)
{
    var res = await http.GetAsync(baseUrl + path);
    var responseBody = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        throw new HttpRequestException($"HTTP {(int)res.StatusCode} for {path}: {responseBody}");
    return string.IsNullOrEmpty(responseBody) ? new JsonObject() : JsonNode.Parse(responseBody)!;
}

var datasetName = $"recipe-07-dataset-dotnet-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
var datasetDescription = $"Capital cities for evaluation (run at {DateTime.UtcNow:O})";
var datasetRows = new[]
{
    new Dictionary<string, object> { ["question"] = "What is the capital of France?", ["answer"] = "Paris" },
    new Dictionary<string, object> { ["question"] = "What is the capital of Spain?", ["answer"] = "Madrid" },
    new Dictionary<string, object> { ["question"] = "What is the capital of Italy?", ["answer"] = "Rome" },
};

// Create the Dataset. v2 path; entity + project go into the URL.
var created = await PostJson($"/v2/{entity}/{project}/datasets", new
{
    name = datasetName,
    description = datasetDescription,
    rows = datasetRows,
});
var objectId = created["object_id"]!.GetValue<string>();
var digest = created["digest"]!.GetValue<string>();
var versionIndex = created["version_index"]!.GetValue<int>();
Console.WriteLine($"Created: object_id={objectId} digest={digest.Substring(0, 12)}… version={versionIndex}");

// Read Dataset metadata back. GET, with object_id + digest in the URL.
var dataset = await GetJson($"/v2/{entity}/{project}/datasets/{objectId}/versions/{digest}");

try
{
    if (dataset["name"]!.GetValue<string>() != datasetName)
        throw new Exception($"name: {dataset["name"]}");
    if (dataset["description"]!.GetValue<string>() != datasetDescription)
        throw new Exception($"description: {dataset["description"]}");
    if (dataset["object_id"]!.GetValue<string>() != objectId)
        throw new Exception($"object_id drift: {dataset["object_id"]}");
    if (dataset["digest"]!.GetValue<string>() != digest)
        throw new Exception($"digest drift: {dataset["digest"]}");
    Console.WriteLine($"Read:    name=\"{dataset["name"]!.GetValue<string>()}\" rows_ref=\"{dataset["rows"]!.GetValue<string>()}\"");

    // The rows field is a reference to a Table. Parse out the table digest
    // so we can /table/query it. The format observed in practice is a
    // weave URI like `weave:///{entity}/{project}/table/{digest}`; tolerate
    // the bare-digest form too in case the shape varies.
    var rowsRef = dataset["rows"]!.GetValue<string>();
    var m = Regex.Match(rowsRef, @"/table/([A-Za-z0-9_-]+)$");
    var tableDigest = m.Success ? m.Groups[1].Value : rowsRef;
    Console.WriteLine($"Table digest: {tableDigest.Substring(0, 12)}…");

    // Query the actual rows.
    var table = await PostJson("/table/query", new { project_id = projectId, digest = tableDigest });
    var rows = table["rows"]!.AsArray();

    // --- verification ---
    // Row count + first-row content must match what we wrote.
    if (rows.Count != datasetRows.Length)
        throw new Exception($"row count: {rows.Count} vs {datasetRows.Length}");

    for (var i = 0; i < datasetRows.Length; i++)
    {
        // Row wrappers carry the row digest + the actual value under `val`.
        var expectedJson = JsonSerializer.Serialize(datasetRows[i], jsonOptions);
        var actualJson = rows[i]!["val"]!.ToJsonString();
        if (actualJson != expectedJson)
            throw new Exception($"row {i} val: {rows[i]!["val"]} vs {expectedJson}");
    }

    Console.WriteLine($"Verified: {rows.Count} rows match (first: {rows[0]!["val"]!.ToJsonString()})");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
