// Recipe 06: attach feedback to many Calls in one request.
//
// Demonstrates the bulk variant of /feedback/create:
//   POST /feedback/batch/create  -> N feedback items in one round trip
//
// Two wire-level points worth knowing:
//
// - The path is `/feedback/batch/create`, not the more guessable
//   `/feedback/create-batch` or `/feedback/createBatch`.
// - The body wraps a parallel-indexed array under `batch`:
//       {"batch": [<FeedbackCreateReq>, <FeedbackCreateReq>, ...]}
//   Each item carries its own `project_id`, `weave_ref`, `feedback_type`,
//   and `payload` — exactly the shape /feedback/create takes. The
//   response mirrors the input with {"res": [<FeedbackCreateRes>, ...]},
//   indices aligned to the input batch.
//
// When to reach for batch over the per-item endpoint:
//
// - Bulk-annotate a list of Calls after a review pass (this recipe's
//   shape — one note per Call).
// - Dump multiple feedback items at the end of a turn (scorer outputs,
//   then notes, then ...).
// - Anywhere round-trip count matters (many small items, latency-bound
//   uploader).
//
// This recipe creates three Calls and attaches *two feedback items per
// Call* in a single batch request: a `wandb.note.1` (UI-visible in the
// trace table) and a custom scorer-style feedback (queryable via
// /feedback/query but not surfaced in the trace table). One round trip
// ships 6 items; the same shape via per-item /feedback/create would
// require 6 round trips.
//
// This mirrors recipe 05's note + scorer split — same pair, but bulk.
//
// Run:
//   dotnet run dotnet/06_BatchFeedback.cs

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

var baseAttributes = new Dictionary<string, object>
{
    ["cookbook.language"] = "dotnet",
    ["cookbook.recipe"] = "06_batch_feedback",
    ["cookbook.environment"] = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev",
};
const string noteType = "wandb.note.1";
const string scorerType = "recipe-06-scorer-correctness";

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

async Task<string> StartCall(string opName, Dictionary<string, object> inputs)
{
    var res = await PostJson("/call/start", new
    {
        start = new
        {
            project_id = projectId,
            op_name = opName,
            started_at = DateTime.UtcNow.ToString("O"),
            attributes = baseAttributes,
            inputs,
        },
    });
    return res["id"]!.GetValue<string>();
}

async Task EndCall(string callId, Dictionary<string, object> output)
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

// Create three Calls — same shape as recipe 01, just repeated.
var questions = new (string Question, string Answer)[]
{
    ("What is the capital of France?", "Paris"),
    ("What is the capital of Spain?", "Madrid"),
    ("What is the capital of Italy?", "Rome"),
};

var calls = new List<(string Id, string Ref, string Answer)>();
for (var i = 0; i < questions.Length; i++)
{
    var (question, answer) = questions[i];
    var callId = await StartCall(
        $"recipe-06-call-{i + 1}",
        new Dictionary<string, object> { ["question"] = question });
    await EndCall(callId, new Dictionary<string, object> { ["answer"] = answer });
    var callRef = $"weave:///{projectId}/call/{callId}";
    calls.Add((callId, callRef, answer));
    Console.WriteLine($"Call {i + 1}: id={callId}");
}

// Build the batch — note + scorer feedback per Call (6 items total).
var batch = calls.SelectMany(c => new object[]
{
    new
    {
        project_id = projectId,
        weave_ref = c.Ref,
        feedback_type = noteType,
        payload = (object)new Dictionary<string, object>
        {
            ["note"] = $"Reviewed — answer: '{c.Answer}'",
        },
    },
    new
    {
        project_id = projectId,
        weave_ref = c.Ref,
        feedback_type = scorerType,
        payload = (object)new Dictionary<string, object>
        {
            ["output"] = new Dictionary<string, object>
            {
                ["score"] = 1.0,
                ["reason"] = $"Answer '{c.Answer}' matches expected",
            },
        },
    },
}).ToArray();

// Single round trip for all 6 items.
var batchRes = await PostJson("/feedback/batch/create", new { batch });
var results = batchRes["res"]!.AsArray();
if (results.Count != batch.Length)
    throw new Exception($"batch size mismatch: sent {batch.Length} got {results.Count}");
for (var i = 0; i < batch.Length; i++)
{
    var fbType = (string)batch[i].GetType().GetProperty("feedback_type")!.GetValue(batch[i])!;
    Console.WriteLine($"Batch->Feedback: type={fbType} feedback_id={results[i]!["id"]!.GetValue<string>()}");
}

// --- verification ---
// For each Call, query feedback by weave_ref and assert both the note
// and the scorer feedback landed with the expected payload. Brief retry
// tolerates eventual consistency in the read path.
var expectedTypes = new HashSet<string> { noteType, scorerType };
try
{
    foreach (var call in calls)
    {
        // $-prefixed keys require JsonObject (anonymous-object property
        // names in C# can't start with $).
        var queryBody = new JsonObject
        {
            ["project_id"] = projectId,
            ["query"] = new JsonObject
            {
                ["$expr"] = new JsonObject
                {
                    ["$eq"] = new JsonArray(
                        new JsonObject { ["$getField"] = "weave_ref" },
                        new JsonObject { ["$literal"] = call.Ref }
                    ),
                },
            },
        };

        var byType = new Dictionary<string, JsonNode>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var res = await PostJson("/feedback/query", queryBody);
            var rows = res["result"]?.AsArray();
            byType = new Dictionary<string, JsonNode>();
            if (rows != null)
                foreach (var row in rows)
                {
                    var t = row?["feedback_type"]?.GetValue<string?>();
                    if (t != null && expectedTypes.Contains(t) && row != null) byType[t] = row;
                }
            if (expectedTypes.IsSubsetOf(byType.Keys)) break;
            Thread.Sleep(1000);
        }

        if (!expectedTypes.IsSubsetOf(byType.Keys))
            throw new Exception($"feedback for {call.Ref} not all visible after 5 reads (got: {string.Join(", ", byType.Keys)})");

        var expectedNote = new Dictionary<string, object>
        {
            ["note"] = $"Reviewed — answer: '{call.Answer}'",
        };
        var expectedScorer = new Dictionary<string, object>
        {
            ["output"] = new Dictionary<string, object>
            {
                ["score"] = 1.0,
                ["reason"] = $"Answer '{call.Answer}' matches expected",
            },
        };
        var expectedNoteJson = JsonSerializer.Serialize(expectedNote, jsonOptions);
        var expectedScorerJson = JsonSerializer.Serialize(expectedScorer, jsonOptions);
        if (byType[noteType]["payload"]!.ToJsonString() != expectedNoteJson)
            throw new Exception($"note payload for {call.Id}: {byType[noteType]["payload"]}");
        if (byType[scorerType]["payload"]!.ToJsonString() != expectedScorerJson)
            throw new Exception($"scorer payload for {call.Id}: {byType[scorerType]["payload"]}");
        foreach (var row in byType.Values)
            if (row["weave_ref"]!.GetValue<string>() != call.Ref)
                throw new Exception($"weave_ref drift: {row["weave_ref"]}");
    }

    Console.WriteLine($"Verified: {batch.Length} batched feedback items across {calls.Count} Calls (note + scorer each)");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
