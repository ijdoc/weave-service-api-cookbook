// Recipe 05: attach feedback to a Call.
//
// Demonstrates the feedback lifecycle:
//   POST /feedback/create  -> attach feedback to a Call
//   POST /feedback/query   -> read it back
//
// Three wire-level points worth knowing:
//
// - The Call is identified by `weave_ref`, not `call_id` directly:
//       weave:///{entity}/{project}/call/{call_id}
//   The recipe constructs this URI inline. There is also a `call_ref`
//   field, but `weave_ref` is the required one.
// - /feedback/create body is *flat* — top-level `project_id`,
//   `weave_ref`, `feedback_type`, `payload` (no wrapper key, like
//   /call/update; unlike /call/start and /call/end).
// - /feedback/query uses the typed Query language. Filtering by
//   `weave_ref` looks like:
//       {"$expr": {"$eq": [
//         {"$getField": "weave_ref"},
//         {"$literal": "weave:///..."}
//       ]}}
//
// `feedback_type` is a freeform string. By convention:
// - `wandb.<kind>.<version>` is reserved for W&B-recognized types that
//   get UI treatment (e.g., `wandb.note.1`, `wandb.reaction.1`).
// - Scorer-emitted feedback typically uses the scorer's name as a prefix
//   so it's distinguishable from human annotation.
//
// This recipe attaches one of each to the same Call.
//
// Run:
//   dotnet run dotnet/05_AddFeedback.cs

using System;
using System.Collections.Generic;
using System.Linq;
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

const string opName = "recipe-05-add-feedback";
var attributes = new Dictionary<string, object>
{
    ["cookbook.language"] = "dotnet",
    ["cookbook.recipe"] = "05_add_feedback",
    ["cookbook.environment"] = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev",
};
var inputs = new Dictionary<string, object> { ["question"] = "What is the capital of Germany?" };
var output = new Dictionary<string, object> { ["answer"] = "Berlin" };

// Two feedback items, illustrating the type-convention split.
const string humanType = "wandb.note.1";
const string scorerType = "recipe-05-scorer-correctness";
var feedback = new (string Type, object Payload)[]
{
    (humanType, new Dictionary<string, object> { ["note"] = "Answer looks correct." }),
    (scorerType, new Dictionary<string, object>
    {
        ["output"] = new Dictionary<string, object>
        {
            ["score"] = 1.0,
            ["reason"] = "Answer matches expected",
        },
    }),
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

// Open the Call.
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
Console.WriteLine($"Started: id={callId}");

// Close it.
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
Console.WriteLine($"Ended:   id={callId}");

// Build the Call's weave_ref. /feedback/create takes this URI string,
// not a raw call_id.
var callRef = $"weave:///{projectId}/call/{callId}";

// Attach both feedback items.
foreach (var fb in feedback)
{
    var res = await PostJson("/feedback/create", new
    {
        project_id = projectId,
        weave_ref = callRef,
        feedback_type = fb.Type,
        payload = fb.Payload,
    });
    Console.WriteLine($"Feedback: id={res["id"]!.GetValue<string>()} type={fb.Type}");
}

// --- verification ---
// Query feedback filtered to this Call by weave_ref, asserting both
// items land with the expected feedback_type + payload. Brief retry
// tolerates eventual consistency in the read path.
var expectedTypes = new HashSet<string> { humanType, scorerType };
// The Query language uses keys prefixed with `$` (e.g. `$expr`, `$eq`,
// `$getField`, `$literal`). C# anonymous-object property names can't
// start with `$`, so build this body as a JsonObject literal.
var queryBody = new JsonObject
{
    ["project_id"] = projectId,
    ["query"] = new JsonObject
    {
        ["$expr"] = new JsonObject
        {
            ["$eq"] = new JsonArray(
                new JsonObject { ["$getField"] = "weave_ref" },
                new JsonObject { ["$literal"] = callRef }
            ),
        },
    },
};

JsonArray? rows = null;
for (var i = 0; i < 5; i++)
{
    var res = await PostJson("/feedback/query", queryBody);
    rows = res["result"]?.AsArray();
    var foundTypes = new HashSet<string>();
    if (rows != null)
        foreach (var row in rows)
            if (row?["feedback_type"]?.GetValue<string?>() is string t) foundTypes.Add(t);
    if (expectedTypes.IsSubsetOf(foundTypes)) break;
    Thread.Sleep(1000);
}

try
{
    if (rows is null)
        throw new Exception($"feedback for {callRef} not visible after 5 reads");
    var byType = new Dictionary<string, JsonNode>();
    foreach (var row in rows)
    {
        var t = row?["feedback_type"]?.GetValue<string?>();
        if (t != null && row != null) byType[t] = row;
    }
    if (!expectedTypes.IsSubsetOf(byType.Keys))
        throw new Exception($"feedback for {callRef} not all visible after 5 reads (got: {string.Join(", ", byType.Keys)})");

    foreach (var fb in feedback)
    {
        var expectedJson = JsonSerializer.Serialize(fb.Payload, jsonOptions);
        var actualJson = byType[fb.Type]["payload"]!.ToJsonString();
        if (actualJson != expectedJson)
            throw new Exception($"payload for {fb.Type}: {byType[fb.Type]["payload"]}");
    }
    foreach (var row in rows)
        if (row!["weave_ref"]!.GetValue<string>() != callRef)
            throw new Exception($"weave_ref drift: {row["weave_ref"]}");

    Console.WriteLine($"Verified: {byType.Count} feedback items on {callRef}");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
