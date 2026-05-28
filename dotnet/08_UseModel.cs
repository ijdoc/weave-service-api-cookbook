// Recipe 08: create a versioned Model + use it in a trace.
//
// First application of *ADR-0004* (the source-embedding scaffold). The
// recipe creates two Weave Objects:
//
//     POST /v2/{entity}/{project}/ops   -> register the predict Op
//                                          (Python scaffold per ADR-0004)
//     POST /obj/create                  -> register the Model object,
//                                          pointing val.predict at the
//                                          predict Op's weave:// ref
//
// Then it opens a Call that references both — establishing the
// "predict logic lives in the recipe file; Weave records identity +
// invocation" pattern that recipes 09–12 reuse.
//
// Three wire-level points worth knowing:
//
// - *The Model is created via `/obj/create`, NOT `/v2/.../models`.*
//   The specialized endpoint stashes the entire source into
//   `files.obj.py` as a single "code tab" attachment and does NOT add
//   per-method ref fields. The W&B UI's Model page renders methods
//   inline only when the val carries a `<method>: <weave:// op ref>`
//   field. The SDK uses the generic Object endpoint with structured
//   metadata for exactly this reason; the cookbook follows suit.
// - The Model val mirrors the SDK shape: `_bases=["Model", "Object",
//   "BaseModel"]`, `_class_name=<subclass>`, `_type=<subclass>`, a
//   `predict` field pointing at the predict Op's weave:// ref, plus
//   *instance attributes that represent the model's instantiation
//   config*. Realistic attributes here are `model_name`, `temperature`,
//   `max_tokens` — the values that distinguish one Model version from
//   another. *Per-Call data* like the question being asked and the
//   answer returned live in the Call's inputs / output, NOT on the
//   Model. Editing a Model attribute is a versioning event; logging a
//   new Call is not.
// - The UI's CallPage parses `op_name` and `inputs.self` as weave://
//   URIs and crashes on raw strings — both MUST be real refs.
//
// Editing this file changes its SHA256 -> the Op scaffold changes ->
// Weave bumps the predict Op's version_index. Per-language identity
// comes from the Model + Op object_ids (`recipe-08-model-<lang>` and
// `recipe-08-model-<lang>.predict`).
//
// For brevity this recipe mocks the actual LLM invocation — the Call's
// output is a hardcoded answer. A real recipe would call the LLM named
// in `model_name` with the Model's `temperature` / `max_tokens`
// settings and the rendered prompt (recipe 07 covers prompts), then
// surface the response.
//
// Run:
//   dotnet run dotnet/08_UseModel.cs

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

// --- ADR-0004 scaffold for the predict Op ---
// SHA256 of this recipe file's bytes. Edits flow through to opSource
// below, which is what Weave content-addresses on. Re-running an
// unchanged file is idempotent; editing bumps the predict Op version.
const string recipePath = "dotnet/08_UseModel.cs";
// __FILE__ equivalent: walk up from cwd to find the recipe file.
// (dotnet run sets cwd to the project's root.)
string recipeAbsPath = Path.Combine(Directory.GetCurrentDirectory(), recipePath);
byte[] recipeBytes = File.ReadAllBytes(recipeAbsPath);
string recipeSha;
using (var sha = SHA256.Create())
{
    recipeSha = Convert.ToHexString(sha.ComputeHash(recipeBytes)).ToLowerInvariant().Substring(0, 16);
}

string opSource = $"""
# Cookbook scaffold (dotnet)
# Source: {recipePath}
# SHA256: {recipeSha}

import weave


@weave.op
def predict(self, question):
    \"\"\"The actual predict implementation lives in:
        {recipePath}

    Byte-for-byte reference (SHA256 of the recipe file):
        {recipeSha}

    To verify a local copy of the file matches (POSIX shell):
        shasum -a 256 {recipePath} | cut -c1-16

    This Python op is a metadata handle, not the real model — running
    it raises NotImplementedError by design.
    \"\"\"
    raise NotImplementedError(
        "This op is a Python scaffold uploaded from a non-Python recipe. "
        "See the docstring above for the real source-language file and a "
        "verifiable byte-for-byte reference (SHA256)."
    )
""";

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

// 1) Register the predict Op via the specialized /v2/.../ops endpoint.
const string opName = "recipe-08-model-dotnet.predict";
var opRes = await PostJson($"/v2/{entity}/{project}/ops", new
{
    name = opName,
    source_code = opSource,
});
var opObjectId = opRes["object_id"]!.GetValue<string>();
var opDigest = opRes["digest"]!.GetValue<string>();
var opVersionIndex = opRes["version_index"]!.GetValue<int>();
var predictOpRef = $"weave:///{projectId}/op/{opObjectId}:{opDigest}";
Console.WriteLine($"Predict op: {opObjectId} digest={opDigest.Substring(0, 12)}… version={opVersionIndex}");

// 2) Register the Model via the generic /obj/create endpoint.
// Instance attributes here are the kind of config a real Model would
// carry — change any value and you get a new (digest, version_index).
// Q&A specifics (the question, the answer) belong on the Call, not the
// Model.
const string modelObjectId = "recipe-08-model-dotnet";
var modelVal = new Dictionary<string, object?>
{
    ["_bases"] = new[] { "Model", "Object", "BaseModel" },
    ["_class_name"] = "Recipe08DotnetModel",
    ["_type"] = "Recipe08DotnetModel",
    ["name"] = modelObjectId,
    ["description"] = "Cookbook model handle (dotnet recipe 08)",
    ["model_name"] = "gpt-4o-mini",
    ["temperature"] = 0.7,
    ["max_tokens"] = 100,
    ["predict"] = predictOpRef,
};
var modelRes = await PostJson("/obj/create", new
{
    obj = new
    {
        project_id = projectId,
        object_id = modelObjectId,
        val = modelVal,
    },
});
var modelDigest = modelRes["digest"]!.GetValue<string>();
var modelRef = $"weave:///{projectId}/object/{modelObjectId}:{modelDigest}";
Console.WriteLine($"Model:      {modelObjectId} digest={modelDigest.Substring(0, 12)}…");
Console.WriteLine($"  ref: {modelRef}");

// 3) Open a Call that uses the predict Op + Model.
const string question = "Is the sky blue?";
var started = await PostJson("/call/start", new
{
    start = new
    {
        project_id = projectId,
        op_name = predictOpRef,
        started_at = DateTime.UtcNow.ToString("O"),
        attributes = new Dictionary<string, object>
        {
            ["cookbook.language"] = "dotnet",
            ["cookbook.recipe"] = "08_use_model",
            ["cookbook.environment"] = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev",
        },
        inputs = new Dictionary<string, object> { ["self"] = modelRef, ["question"] = question },
    },
});
var callId = started["id"]!.GetValue<string>();
var traceId = started["trace_id"]!.GetValue<string>();
Console.WriteLine($"Started:    id={callId}");

// 4) End the Call with the model's answer.
// A real recipe would call modelVal["model_name"] here with the
// question and the model's temperature/max_tokens settings, and use
// the LLM's response as the Call's output. We hardcode an answer so
// the cookbook stays focused on the wire-level Model + Op + Call
// wiring.
const string answer = "yes";
await PostJson("/call/end", new
{
    end = new
    {
        project_id = projectId,
        id = callId,
        ended_at = DateTime.UtcNow.ToString("O"),
        summary = new
        {
            status_counts = new { success = 1, error = 0 },
            weave = new { status = "success", trace_name = opName },
        },
        output = answer,
    },
});
Console.WriteLine($"Ended:      id={callId} output=\"{answer}\"");

// --- verification ---
JsonNode? call = null;
for (var i = 0; i < 5; i++)
{
    var r = await PostJson("/call/read", new { project_id = projectId, id = callId });
    call = r["call"];
    if (call != null && call["ended_at"]?.GetValue<string?>() != null) break;
    Thread.Sleep(1000);
}

try
{
    if (call is null || call["ended_at"]?.GetValue<string?>() is null)
        throw new Exception($"Call {callId} not visible/finished after 5 reads");

    if (call["op_name"]!.GetValue<string>() != predictOpRef)
        throw new Exception($"op_name: {call["op_name"]}");
    if (call["inputs"]!["self"]!.GetValue<string>() != modelRef)
        throw new Exception($"inputs.self: {call["inputs"]!["self"]}");
    if (call["inputs"]!["question"]!.GetValue<string>() != question)
        throw new Exception($"inputs.question: {call["inputs"]!["question"]}");
    if (call["output"]!.GetValue<string>() != answer)
        throw new Exception($"output: {call["output"]}");
    if (call["trace_id"]!.GetValue<string>() != traceId)
        throw new Exception($"trace_id: {call["trace_id"]}");

    Console.WriteLine($"Verified:   id={callId} (op + model + output round-tripped)");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
