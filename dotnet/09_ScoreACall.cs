// Recipe 09: create a Scorer Op + score a Call (the apply_scorer pattern).
//
// Wire-level equivalent of the SDK's `result.call.apply_scorer(scorer)`
// pattern — score an arbitrary already-logged Call without dragging in
// the full evaluation flow (recipes 11-13). Reuses the ADR-0004
// Op-creation pattern from recipe 08, this time for a scorer function.
//
// A *Scorer Op* is just an Op whose role is to score a Call's output.
// There is no separate Scorer Object class to register here — the W&B
// service does expose `POST /v2/.../scorers` (a dedicated Scorer object
// endpoint), but the cookbook does not use it; the Op pattern is what
// `@weave.op` scorer functions use and what `apply_scorer` integrates
// with under the hood.
//
// This recipe builds three things on the wire:
//
// 1. A small model Call producing a sample prediction (mirrors recipe
//    08's predict shape but simpler — we skip the Model object and the
//    predict Op, just open a Call directly).
// 2. A scoring Call invoking the Scorer Op, with the prediction +
//    expected answer as inputs and the score value as output. This is
//    a top-level standalone Call (no parent_id; separate trace) — same
//    shape `apply_scorer` produces.
// 3. A *`wandb.runnable.<scorer_op_id>`* Feedback row attached to the
//    prediction Call. *This Feedback is the load-bearing link that
//    makes the score render inline under the prediction in the W&B UI.*
//    Without it, the score Call would be a disconnected island.
//
// Wire-level points worth knowing:
//
// - The *`wandb.runnable.*`* Feedback convention is how SDK
//   `apply_scorer` ties a standalone scoring Call back to a prediction
//   Call. The Feedback row carries:
//       feedback_type = "wandb.runnable.<scorer_op_id>"
//       payload       = {"output": <score value>}
//       runnable_ref  = <Scorer Op weave:// ref>
//       call_ref      = <score Call weave:// ref>
//   The UI reads `wandb.runnable.*` Feedbacks on the prediction Call
//   and shows the score (plus a link to the score Call). This is the
//   same Feedback endpoint family covered in recipes 05-06, just with
//   a specific feedback_type pattern Weave recognises.
// - Scorer-Op scoring (this recipe) and plain `feedback_type` scoring
//   (recipe 06 — `wandb.note.1`, `wandb.reaction.1`, arbitrary user
//   types) coexist. The structured eval flow (recipe 12) uses scorer
//   Ops + nested children under `Evaluation.predict_and_score`, plus
//   matching Feedback rows. Recipe 09 is the standalone apply-scorer-
//   to-an-existing-call shape.
// - Scorer Op object_ids are NOT aggregator-filtered, so per-language
//   naming (`recipe-09-is-correct-{python,ruby,dotnet}`) is fine. The
//   canonical Eval Op names in recipe 12 (`Evaluation.evaluate` etc.)
//   *are* aggregator-filtered, which is why those stay shared.
// - The Scorer Op's source carries the ADR-0004 scaffold (header +
//   in-method docstring + raise NotImplementedError + shasum verify
//   hint).
//
// Run:
//   dotnet run dotnet/09_ScoreACall.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
string entity = Environment.GetEnvironmentVariable("WANDB_ENTITY")!;
string project = Environment.GetEnvironmentVariable("WANDB_PROJECT")!;
string projectId = $"{entity}/{project}";

// --- ADR-0004 scaffold for the Scorer Op ---
const string recipePath = "dotnet/09_ScoreACall.cs";
string recipeAbsPath = Path.Combine(Directory.GetCurrentDirectory(), recipePath);
byte[] recipeBytes = File.ReadAllBytes(recipeAbsPath);
string recipeSha;
using (var sha = SHA256.Create())
{
    recipeSha = Convert.ToHexString(sha.ComputeHash(recipeBytes)).ToLowerInvariant().Substring(0, 16);
}

// C# 11 raw-string-with-interpolation: the outer ${"""" delimiter (four
// quotes) lets us include literal triple-quote (""") for the Python
// docstring without escaping. Inside this literal, backslashes are
// literal characters — using \" would actually upload a backslash.
string scorerSource = $""""
# Cookbook scaffold (dotnet)
# Source: {recipePath}
# SHA256: {recipeSha}

import weave


@weave.op
def is_correct(output, expected):
    """The actual scoring implementation lives in:
        {recipePath}

    Byte-for-byte reference (SHA256 of the recipe file):
        {recipeSha}

    To verify a local copy of the file matches (POSIX shell):
        shasum -a 256 {recipePath} | cut -c1-16

    This Python op is a metadata handle, not the real scorer — running
    it raises NotImplementedError by design.
    """
    raise NotImplementedError(
        "This op is a Python scaffold uploaded from a non-Python recipe. "
        "See the docstring above for the real source-language file and a "
        "verifiable byte-for-byte reference (SHA256)."
    )
"""";

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

// 1) Register the Scorer Op. Per-language object_id; the server
// lowercases it. Per the docstring, Scorer Op names are not
// aggregator-filtered, so per-language identity is fine.
const string scorerOpId = "recipe-09-is-correct-dotnet";
var scorerRes = await PostJson($"/v2/{entity}/{project}/ops", new
{
    name = scorerOpId,
    source_code = scorerSource,
});
var scorerOpObjectId = scorerRes["object_id"]!.GetValue<string>();
var scorerOpDigest = scorerRes["digest"]!.GetValue<string>();
var scorerOpVersionIndex = scorerRes["version_index"]!.GetValue<int>();
var scorerOpRef = $"weave:///{projectId}/op/{scorerOpObjectId}:{scorerOpDigest}";
Console.WriteLine($"Scorer op:  {scorerOpObjectId} digest={scorerOpDigest.Substring(0, 12)}… version={scorerOpVersionIndex}");

// 2) Produce a sample prediction via a tiny model Call, then score it
// with the Scorer Op as a SEPARATE top-level Call. The link between
// them isn't structural (no parent_id) — it's a `wandb.runnable.*`
// Feedback row created in step 4, mirroring what the SDK's
// `apply_scorer` does under the hood.
const string question = "Is the sky blue?";
const string expected = "yes";

var predictStarted = await PostJson("/call/start", new
{
    start = new
    {
        project_id = projectId,
        op_name = "recipe-09-mock-predict",
        started_at = DateTime.UtcNow.ToString("O"),
        attributes = new Dictionary<string, object>
        {
            ["cookbook.language"] = "dotnet",
            ["cookbook.recipe"] = "09_score_a_call",
            ["cookbook.environment"] = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev",
        },
        inputs = new Dictionary<string, object> { ["question"] = question },
    },
});
var predictCallId = predictStarted["id"]!.GetValue<string>();
var traceId = predictStarted["trace_id"]!.GetValue<string>();
const string prediction = "yes";
await PostJson("/call/end", new
{
    end = new
    {
        project_id = projectId,
        id = predictCallId,
        ended_at = DateTime.UtcNow.ToString("O"),
        summary = new
        {
            status_counts = new { success = 1, error = 0 },
            weave = new { status = "success", trace_name = "recipe-09-mock-predict" },
        },
        // Per the cookbook's question/answer convention (CONTRIBUTING.md),
        // predict outputs land under an `answer` key. The Scorer Op below
        // still takes the raw answer value as its `output` argument —
        // that's the scorer's signature, not the predict's output shape.
        output = new Dictionary<string, object> { ["answer"] = prediction },
    },
});
Console.WriteLine($"Predicted:  id={predictCallId} output=\"{prediction}\"");

// 3) Open a top-level scoring Call invoking the Scorer Op. op_name MUST
// be the Op's weave:// ref (not a bare string) for the UI to render
// the Op inline. Inputs are what's being scored (prediction +
// expected); output is the score value (boolean here — Eval Result
// aggregation in recipe 13 classifies this as a binary value type).
//
// Inputs use raw values here for simplicity. In the full eval flow
// (recipe 12), the SDK refs Dataset row fields and Model attributes
// via weave:// URIs so the UI can navigate back to the source — see
// recipe 12 for that richer shape.
var scoreStarted = await PostJson("/call/start", new
{
    start = new
    {
        project_id = projectId,
        op_name = scorerOpRef,
        started_at = DateTime.UtcNow.ToString("O"),
        attributes = new Dictionary<string, object>
        {
            ["cookbook.language"] = "dotnet",
            ["cookbook.recipe"] = "09_score_a_call",
            ["cookbook.environment"] = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev",
        },
        inputs = new Dictionary<string, object> { ["output"] = prediction, ["expected"] = expected },
    },
});
var scoreCallId = scoreStarted["id"]!.GetValue<string>();
bool score = prediction == expected;
await PostJson("/call/end", new
{
    end = new
    {
        project_id = projectId,
        id = scoreCallId,
        ended_at = DateTime.UtcNow.ToString("O"),
        summary = new
        {
            status_counts = new { success = 1, error = 0 },
            weave = new { status = "success", trace_name = scorerOpId },
        },
        output = score,
    },
});
Console.WriteLine($"Scored:     id={scoreCallId} output={(score ? "true" : "false")}");

// 4) Link the score to the prediction Call by creating a
// `wandb.runnable.<scorer_op_id>` Feedback row on the prediction.
// This is the load-bearing step — the W&B UI uses this Feedback (not
// any parent-child structure) to render the score inline on the
// prediction Call's view. The SDK's `apply_scorer` posts this exact
// shape under the hood.
var predictCallRef = $"weave:///{projectId}/call/{predictCallId}";
var scoreCallRef = $"weave:///{projectId}/call/{scoreCallId}";
var feedbackRes = await PostJson("/feedback/create", new
{
    project_id = projectId,
    weave_ref = predictCallRef,
    feedback_type = $"wandb.runnable.{scorerOpId}",
    payload = new Dictionary<string, object> { ["output"] = score },
    runnable_ref = scorerOpRef,
    call_ref = scoreCallRef,
});
Console.WriteLine($"Linked:     feedback id={feedbackRes["id"]!.GetValue<string>()} on predict call (feedback_type=wandb.runnable.{scorerOpId})");

// --- verification ---
// (a) The scoring Call round-trips with the right op_ref + inputs +
//     boolean output.
// (b) The wandb.runnable.* Feedback exists on the prediction Call
//     and carries the score value + scorer Op ref + score Call ref.
JsonNode? call = null;
for (var i = 0; i < 5; i++)
{
    var r = await PostJson("/call/read", new { project_id = projectId, id = scoreCallId });
    call = r["call"];
    if (call != null && call["ended_at"]?.GetValue<string?>() != null) break;
    Thread.Sleep(1000);
}

try
{
    if (call is null || call["ended_at"]?.GetValue<string?>() is null)
        throw new Exception($"scoring Call {scoreCallId} not visible/finished after 5 reads");

    if (call["op_name"]!.GetValue<string>() != scorerOpRef)
        throw new Exception($"op_name: {call["op_name"]}");
    if (call["inputs"]!["output"]!.GetValue<string>() != prediction)
        throw new Exception($"inputs.output: {call["inputs"]!["output"]}");
    if (call["inputs"]!["expected"]!.GetValue<string>() != expected)
        throw new Exception($"inputs.expected: {call["inputs"]!["expected"]}");
    if (call["output"]!.GetValue<bool>() != score)
        throw new Exception($"output: {call["output"]}");

    // Verify the wandb.runnable.* Feedback row on the prediction Call.
    var expectedFeedbackType = $"wandb.runnable.{scorerOpId}";
    var queryBody = new JsonObject
    {
        ["project_id"] = projectId,
        ["query"] = new JsonObject
        {
            ["$expr"] = new JsonObject
            {
                ["$eq"] = new JsonArray(
                    new JsonObject { ["$getField"] = "weave_ref" },
                    new JsonObject { ["$literal"] = predictCallRef }
                ),
            },
        },
    };
    JsonArray? feedbackRows = null;
    JsonNode? linking = null;
    for (var i = 0; i < 5; i++)
    {
        var r = await PostJson("/feedback/query", queryBody);
        feedbackRows = r["result"]?.AsArray();
        if (feedbackRows != null)
        {
            foreach (var row in feedbackRows)
            {
                if (row?["feedback_type"]?.GetValue<string?>() == expectedFeedbackType)
                {
                    linking = row;
                    break;
                }
            }
            if (linking != null) break;
        }
        Thread.Sleep(1000);
    }
    if (linking is null)
        throw new Exception($"no {expectedFeedbackType} feedback on {predictCallRef} after 5 reads");

    if (linking["payload"]!["output"]!.GetValue<bool>() != score)
        throw new Exception($"payload.output: {linking["payload"]!["output"]}");
    if (linking["runnable_ref"]!.GetValue<string>() != scorerOpRef)
        throw new Exception($"runnable_ref: {linking["runnable_ref"]}");
    if (linking["call_ref"]!.GetValue<string>() != scoreCallRef)
        throw new Exception($"call_ref: {linking["call_ref"]}");

    Console.WriteLine($"Verified:   id={scoreCallId} (scorer op + inputs + score output round-tripped)");
    Console.WriteLine($"Verified:   wandb.runnable.{scorerOpId} feedback links score -> predict");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
