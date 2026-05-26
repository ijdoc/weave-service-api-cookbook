// Recipe 03: parent + child Calls (RAG-shaped trace).
//
// Demonstrates Trace structure: one parent Call with two child Calls
// underneath. Children declare their parent via `parent_id` on
// /call/start and share the parent's `trace_id` explicitly.
//
// The RAG-shaped flow:
//     rag_pipeline (parent)
//     ├── retrieve  (child 1)
//     └── generate  (child 2)
//
// Ordering matters: a child's /call/start happens after the parent's
// /call/start, and each child's /call/end happens before the parent's
// /call/end. The recipe shows this canonical order.
//
// Run:
//   dotnet run dotnet/03_ParentChildCalls.cs

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

var baseAttributes = new Dictionary<string, object>
{
    ["cookbook.language"] = "dotnet",
    ["cookbook.recipe"] = "03_parent_child_calls",
    ["cookbook.environment"] = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev",
};

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

async Task<JsonNode> StartCall(string opName, object inputs, string? parentId = null, string? traceId = null)
{
    var start = new Dictionary<string, object>
    {
        ["project_id"] = projectId,
        ["op_name"] = opName,
        ["started_at"] = DateTime.UtcNow.ToString("O"),
        ["attributes"] = baseAttributes,
        ["inputs"] = inputs,
    };
    if (parentId is not null) start["parent_id"] = parentId;
    if (traceId is not null) start["trace_id"] = traceId;
    return await PostJson("/call/start", new { start });
}

async Task EndCall(string callId, object output)
{
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
}

// Open the parent (top-level: no parent_id, no explicit trace_id).
// The server assigns a trace_id which we propagate to children.
var parent = await StartCall("recipe-03-rag-pipeline", new { question = "Where is the Eiffel Tower?" });
var parentId = parent["id"]!.GetValue<string>();
var traceId = parent["trace_id"]!.GetValue<string>();
Console.WriteLine($"Started parent: id={parentId} trace_id={traceId}");

// Open + finish the first child (retrieve), passing the parent's id and trace_id.
var retrieve = await StartCall(
    "recipe-03-retrieve",
    new { question = "Where is the Eiffel Tower?" },
    parentId: parentId,
    traceId: traceId);
var retrieveId = retrieve["id"]!.GetValue<string>();
Console.WriteLine($"Started child 1: id={retrieveId}");
await EndCall(retrieveId, new { docs = new[] { "Paris", "France" } });
Console.WriteLine($"Ended   child 1: id={retrieveId}");

// Open + finish the second child (generate).
var generate = await StartCall(
    "recipe-03-generate",
    new { docs = new[] { "Paris", "France" }, question = "Where is the Eiffel Tower?" },
    parentId: parentId,
    traceId: traceId);
var generateId = generate["id"]!.GetValue<string>();
Console.WriteLine($"Started child 2: id={generateId}");
await EndCall(generateId, new { answer = "In Paris, France." });
Console.WriteLine($"Ended   child 2: id={generateId}");

// Close the parent (after all children have finished).
await EndCall(parentId, new { answer = "In Paris, France." });
Console.WriteLine($"Ended   parent:  id={parentId}");

// --- verification ---
var expected = new[] { parentId, retrieveId, generateId };
var foundById = new Dictionary<string, JsonNode>();
for (var attempt = 0; attempt < 5; attempt++)
{
    var rows = await StreamQuery(new
    {
        project_id = projectId,
        filter = new { trace_ids = new[] { traceId } },
    });
    foundById = new Dictionary<string, JsonNode>();
    foreach (var c in rows)
    {
        var id = c["id"]?.GetValue<string?>();
        if (id != null) foundById[id] = c;
    }
    // Require all three visible AND finalized (ended_at populated) so we
    // don't race write-to-read propagation on inner-field reads.
    var allReady = true;
    foreach (var id in expected)
    {
        if (!foundById.TryGetValue(id, out var c) || c["ended_at"]?.GetValue<string?>() is null)
        {
            allReady = false;
            break;
        }
    }
    if (allReady) break;
    Thread.Sleep(1000);
}

try
{
    foreach (var id in expected)
        if (!foundById.ContainsKey(id))
            throw new Exception($"trace {traceId} missing call {id}");

    var parentCall = foundById[parentId];
    var retrieveCall = foundById[retrieveId];
    var generateCall = foundById[generateId];

    if (parentCall["parent_id"]?.GetValue<string?>() != null)
        throw new Exception($"parent has parent_id: {parentCall["parent_id"]}");
    if (retrieveCall["parent_id"]?.GetValue<string>() != parentId)
        throw new Exception($"retrieve.parent_id: {retrieveCall["parent_id"]}");
    if (generateCall["parent_id"]?.GetValue<string>() != parentId)
        throw new Exception($"generate.parent_id: {generateCall["parent_id"]}");

    foreach (var call in new[] { parentCall, retrieveCall, generateCall })
    {
        if (call["trace_id"]!.GetValue<string>() != traceId)
            throw new Exception($"trace_id on {call["id"]}: {call["trace_id"]}");
        foreach (var (k, v) in baseAttributes)
        {
            var actual = call["attributes"]?[k]?.GetValue<string?>();
            if (actual != v.ToString())
                throw new Exception($"attribute {k} on {call["id"]}: {actual}");
        }
    }

    Console.WriteLine($"Verified: trace_id={traceId} (1 parent + 2 children)");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
