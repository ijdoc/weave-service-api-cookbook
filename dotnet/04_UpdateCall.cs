// Recipe 04: update a Call's display_name after it finishes.
//
// Demonstrates the only mutation the service API exposes on a finished
// Call:
//   POST /call/update  -> change display_name
//
// Two wire-level quirks worth noting:
//
// - The body is *flat*: top-level `project_id`, `call_id`, `display_name`.
//   /call/start and /call/end wrap their bodies under `start` / `end`;
//   /call/update does not. Sending {"update": {...}} will 422.
// - The id field is named `call_id`, not `id` (which is what /call/end
//   uses).
//
// The schema's other constraint is that `display_name` is the only
// user-modifiable field. `attributes`, `inputs`, `output`, etc. are
// immutable after /call/start.
//
// Run:
//   dotnet run dotnet/04_UpdateCall.cs

using System;
using System.Collections.Generic;
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

const string opName = "recipe-04-update-call";
var attributes = new Dictionary<string, object>
{
    ["cookbook.language"] = "dotnet",
    ["cookbook.recipe"] = "04_update_call",
    ["cookbook.environment"] = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev",
};
var inputs = new Dictionary<string, object> { ["question"] = "What is the capital of Italy?" };
var output = new Dictionary<string, object> { ["answer"] = "Rome" };
const string newDisplayName = "recipe 04 — updated after finish";

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
var traceId = started["trace_id"]!.GetValue<string>();
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

// Mutate display_name. Flat body, `call_id` (not `id`), no wrapper key.
await PostJson("/call/update", new
{
    project_id = projectId,
    call_id = callId,
    display_name = newDisplayName,
});
Console.WriteLine($"Updated: id={callId} display_name=\"{newDisplayName}\"");

// --- verification ---
// Read the Call back and assert display_name reflects the update.
// Brief retry loop tolerates eventual consistency in the read path.
JsonNode? call = null;
for (var i = 0; i < 5; i++)
{
    var read = await PostJson("/call/read", new { project_id = projectId, id = callId });
    call = read["call"];
    if (call != null && call["display_name"]?.GetValue<string?>() == newDisplayName) break;
    Thread.Sleep(1000);
}

try
{
    if (call is null || call["display_name"]?.GetValue<string?>() != newDisplayName)
        throw new Exception($"Call {callId} display_name not updated after 5 reads");

    if (call["display_name"]!.GetValue<string>() != newDisplayName)
        throw new Exception($"display_name: {call["display_name"]}");
    // op_name and the rest must NOT have changed — /call/update only touches display_name.
    if (call["op_name"]!.GetValue<string>() != opName)
        throw new Exception($"op_name drifted: {call["op_name"]}");
    foreach (var (k, v) in attributes)
    {
        var actual = call["attributes"]?[k]?.GetValue<string?>();
        if (actual != v.ToString())
            throw new Exception($"attribute {k}: {actual}");
    }
    if (call["inputs"]?["question"]?.GetValue<string?>() != "What is the capital of Italy?")
        throw new Exception($"inputs: {call["inputs"]}");
    if (call["output"]?["answer"]?.GetValue<string?>() != "Rome")
        throw new Exception($"output: {call["output"]}");
    if (call["trace_id"]!.GetValue<string>() != traceId)
        throw new Exception($"trace_id: {call["trace_id"]}");

    Console.WriteLine($"Verified: id={callId} display_name=\"{call["display_name"]!.GetValue<string>()}\"");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
