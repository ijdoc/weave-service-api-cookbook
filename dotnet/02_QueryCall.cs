// Recipe 02: query Calls via /calls/stream_query.
//
// Demonstrates the workhorse read endpoint:
//   POST /calls/stream_query  -> stream NDJSON of matching Calls
//
// Sets up by creating one Call (op_name="recipe-02-query-call"), then
// queries that op_name and confirms the just-created Call appears in
// the streamed results.
//
// The endpoint returns one JSON object per line (application/jsonl).
// We parse line-by-line via StreamReader rather than buffering the
// full response, demonstrating the streaming pattern in HttpClient.
//
// Run:
//   dotnet run dotnet/02_QueryCall.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
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
string projectId = $"{Environment.GetEnvironmentVariable("WANDB_ENTITY")}/{Environment.GetEnvironmentVariable("WANDB_PROJECT")}";

const string opName = "recipe-02-query-call";
var attributes = new Dictionary<string, object>
{
    ["cookbook.language"] = "dotnet",
    ["cookbook.recipe"] = "02_query_call",
    ["cookbook.environment"] = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev",
};
var inputs = new Dictionary<string, object> { ["question"] = "What is the capital of Spain?" };
var output = new Dictionary<string, object> { ["answer"] = "Madrid" };

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

// Streams /calls/stream_query response line-by-line, parsing each
// newline-delimited JSON object as it arrives. Returns the parsed rows.
async Task<List<JsonNode>> StreamQuery(object body)
{
    var json = JsonSerializer.Serialize(body, jsonOptions);
    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/calls/stream_query")
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
    using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    if (!res.IsSuccessStatusCode)
    {
        var errBody = await res.Content.ReadAsStringAsync();
        throw new HttpRequestException($"HTTP {(int)res.StatusCode} for /calls/stream_query: {errBody}");
    }
    var rows = new List<JsonNode>();
    using var stream = await res.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);
    string? line;
    while ((line = await reader.ReadLineAsync()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        rows.Add(JsonNode.Parse(line)!);
    }
    return rows;
}

// Setup: create + end a Call we can later query for.
var started = await PostJson("/call/start", new
{
    start = new
    {
        project_id = projectId,
        op_name = opName,
        started_at = DateTime.UtcNow.ToString("O"),
        attributes,
        inputs,
    },
});
var callId = started["id"]!.GetValue<string>();
var traceId = started["trace_id"]!.GetValue<string>();
Console.WriteLine($"Created: id={callId}");

await PostJson("/call/end", new
{
    end = new
    {
        project_id = projectId,
        id = callId,
        ended_at = DateTime.UtcNow.ToString("O"),
        summary = new { },
        output,
    },
});

// Query: stream Calls matching our op_name, newest first. Retry briefly
// to tolerate eventual consistency on the read path.
JsonNode? found = null;
for (var attempt = 0; attempt < 5; attempt++)
{
    var rows = await StreamQuery(new
    {
        project_id = projectId,
        filter = new { op_names = new[] { opName } },
        sort_by = new[] { new { field = "started_at", direction = "desc" } },
        limit = 50,
    });
    // Require ended_at populated so we don't race the write-to-read
    // propagation and read a half-finalized row.
    found = rows.Find(c => c["id"]?.GetValue<string?>() == callId
                            && c["ended_at"]?.GetValue<string?>() != null);
    if (found != null) break;
    Thread.Sleep(1000);
}

try
{
    if (found is null)
        throw new Exception($"Call {callId} not in stream_query results after 5 attempts");

    if (found["op_name"]!.GetValue<string>() != opName)
        throw new Exception($"op_name: {found["op_name"]}");
    foreach (var (k, v) in attributes)
    {
        var actual = found["attributes"]?[k]?.GetValue<string?>();
        if (actual != v.ToString())
            throw new Exception($"attribute {k}: {actual}");
    }
    if (found["inputs"]?["question"]?.GetValue<string?>() != "What is the capital of Spain?")
        throw new Exception($"inputs: {found["inputs"]}");
    if (found["output"]?["answer"]?.GetValue<string?>() != "Madrid")
        throw new Exception($"output: {found["output"]}");
    if (found["trace_id"]!.GetValue<string>() != traceId)
        throw new Exception($"trace_id: {found["trace_id"]}");

    Console.WriteLine($"Verified: id={callId}");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
