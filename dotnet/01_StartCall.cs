// Recipe 01: start and finish a single Call.
//
// Demonstrates the minimum Call lifecycle:
//   POST /call/start  -> open the Call, capture id + trace_id
//   POST /call/end    -> close it
//
// Then verifies via POST /call/read.
//
// Run:
//   dotnet run dotnet/01_StartCall.cs

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

// .NET 10 file-based programs disable reflection-based JSON (de)serialization
// by default (AOT/trim posture). Setting an explicit TypeInfoResolver here
// re-enables the reflection path, which is what we want for a cookbook
// recipe where readability beats AOT-compatibility.
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

const string opName = "recipe-01-start-call";
var attributes = new Dictionary<string, object>
{
    ["cookbook.language"] = "dotnet",
    ["cookbook.recipe"] = "01_start_call",
};
var inputs = new Dictionary<string, object> { ["question"] = "What is the capital of France?" };
var output = new Dictionary<string, object> { ["answer"] = "Paris" };

using var http = new HttpClient();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Basic",
    Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{apiKey}")));

// Tiny POST helper. Centralizes auth + JSON serialization; the per-call
// payload shape stays visible at the call sites below.
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
Console.WriteLine($"Started: id={callId} trace_id={traceId}");

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

// --- verification ---
// Read the Call back and assert wire-state matches what we sent.
// Brief retry loop tolerates eventual consistency in the read path.
JsonNode? call = null;
for (var i = 0; i < 5; i++)
{
    var read = await PostJson("/call/read", new { project_id = projectId, id = callId });
    call = read["call"];
    if (call != null && call["ended_at"]?.GetValue<string?>() != null) break;
    Thread.Sleep(1000);
}

try
{
    if (call is null || call["ended_at"]?.GetValue<string?>() is null)
        throw new Exception($"Call {callId} not visible/finished after 5 reads");

    if (call["op_name"]!.GetValue<string>() != opName)
        throw new Exception($"op_name: {call["op_name"]}");
    foreach (var (k, v) in attributes)
    {
        var actual = call["attributes"]?[k]?.GetValue<string?>();
        if (actual != v.ToString())
            throw new Exception($"attribute {k}: {actual}");
    }
    if (call["inputs"]?["question"]?.GetValue<string?>() != "What is the capital of France?")
        throw new Exception($"inputs: {call["inputs"]}");
    if (call["output"]?["answer"]?.GetValue<string?>() != "Paris")
        throw new Exception($"output: {call["output"]}");
    if (call["trace_id"]!.GetValue<string>() != traceId)
        throw new Exception($"trace_id: {call["trace_id"]}");

    Console.WriteLine($"Verified: id={callId}");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
