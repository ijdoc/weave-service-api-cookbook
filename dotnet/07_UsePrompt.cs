// Recipe 07: publish a Prompt + reference it from a Call + tag/alias it.
//
// Introduces four new things that recipes 08-13 build on:
//
//     POST /obj/create                            -> generic Weave Object
//                                                    endpoint; here, publish
//                                                    a StringPrompt
//     POST /obj/read                              -> read it back
//     PUT  /objs/{id}/versions/{digest}/tags      -> add version tags
//     PUT  /objs/{id}/aliases                     -> set named pointers
//
//     (and the existing /call/start + /call/end, but now with
//      `inputs.prompt` = a weave:// ref to the Prompt — the "object
//      ref in trace inputs" pattern that unlocks Model.predict,
//      Scorer Ops, and the eval flow)
//
// Five wire-level points worth knowing:
//
// - The Object endpoint is *flat under an `obj` wrapper*:
//       {"obj": {"project_id", "object_id", "val"}}
//   The val you submit is what Weave stores verbatim (after lowercasing
//   the `object_id`). The val MUST carry `_bases`, `_class_name`, and
//   `_type` for the Weave UI to recognise the object — the server does
//   not auto-fill these. An optional `builtin_object_class` field on
//   the request must match val's `_class_name` exactly when set;
//   omitting it is cleaner (the val is the single source of truth on
//   class info).
// - `base_object_class="Prompt"` (what the W&B UI's Prompts page
//   filters on) is derived by the server from `val._bases`;
//   `leaf_object_class` comes from `val._class_name`. A one-line
//   variant for messages-shaped prompts is `MessagesPrompt` (list of
//   `{role, content}` dicts) — not demonstrated here, but the same val
//   shape applies (`_class_name` / `_type` become "MessagesPrompt",
//   and a `messages` field replaces `content`).
// - A Prompt is content-addressed: identical val collapses to the same
//   (digest, version_index). Editing the content (or any other val
//   field) bumps the version. No timestamping needed; this recipe's
//   per-language identity comes from a different `object_id` per port.
// - *Tags vs aliases* — both UI-visible Object metadata, separate from
//   val (so changing them does NOT bump the version):
//     * Tags are per-VERSION, additive labels (e.g., "dev",
//       "production", "reviewed"). PUT adds, POST .../remove removes.
//       Many versions can share a tag.
//     * Aliases are per-object_id named pointers — re-PUTting an alias
//       detaches it from the prior version. The server auto-maintains
//       a `latest` alias on the most-recent version; do not set it
//       yourself.
//   These same endpoints apply to any Weave Object (Model, Dataset,
//   Evaluation, Scorer Op), not just Prompts.
// - *Val "extras"* — you can also stuff arbitrary JSON fields directly
//   into val (any type, nested dicts, etc.) alongside the canonical
//   `content`/`description`/`name`. They round-trip cleanly and are
//   queryable via /objs/query filters, but DO NOT appear in dedicated
//   UI columns or panels — only `tags` and `aliases` do. Use val
//   extras for structured machine-queryable metadata; use tags/aliases
//   for UI-visible labels and pointers.
//
// Run:
//   dotnet run dotnet/07_UsePrompt.cs

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

using var http = new HttpClient();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Basic",
    Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{apiKey}")));

async Task<JsonNode> SendJson(HttpMethod method, string path, object body)
{
    var json = JsonSerializer.Serialize(body, jsonOptions);
    using var req = new HttpRequestMessage(method, baseUrl + path)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
    var res = await http.SendAsync(req);
    var responseBody = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        throw new HttpRequestException($"HTTP {(int)res.StatusCode} for {path}: {responseBody}");
    return string.IsNullOrEmpty(responseBody) ? new JsonObject() : JsonNode.Parse(responseBody)!;
}

Task<JsonNode> PostJson(string path, object body) => SendJson(HttpMethod.Post, path, body);
Task<JsonNode> PutJson(string path, object body) => SendJson(HttpMethod.Put, path, body);

// 1) Publish a StringPrompt via the generic Object endpoint.
//
// val "extras": you could add arbitrary JSON fields here alongside
// the canonical ones below (e.g., "owner_email" = "alice@example.com",
// "model_target" = "gpt-4o-mini", "custom_attributes" = new {...}).
// They'd round-trip cleanly and be queryable via /objs/query filters,
// but would NOT appear in dedicated UI columns. For UI-visible
// metadata, use the tags + aliases steps further down.
const string promptObjectId = "recipe-07-prompt-dotnet";
const string promptContent = "Answer the question concisely: {question}";
var promptVal = new Dictionary<string, object?>
{
    ["_bases"] = new[] { "Prompt", "Object", "BaseModel" },
    ["_class_name"] = "StringPrompt",
    ["_type"] = "StringPrompt",
    ["name"] = null,
    ["description"] = "Capital-city Q&A prompt template (dotnet recipe 07)",
    ["content"] = promptContent,
};
var created = await PostJson("/obj/create", new
{
    obj = new
    {
        project_id = projectId,
        object_id = promptObjectId,
        val = promptVal,
    },
});
var promptDigest = created["digest"]!.GetValue<string>();
var promptRef = $"weave:///{projectId}/object/{promptObjectId}:{promptDigest}";
Console.WriteLine($"Published: {promptObjectId} digest={promptDigest.Substring(0, 12)}…");
Console.WriteLine($"  ref: {promptRef}");

// 2) Tag this version with the current cookbook environment ("dev" or
// "ci"). Tags are a first-class, per-version, UI-visible metadata
// channel — separate from val. PUT is additive (re-runs are no-ops if
// the tag is already present); removal uses POST /objs/.../tags/remove
// with the same body shape. The same endpoint applies to any Weave
// Object (Model, Dataset, Evaluation, Scorer Op).
var envTag = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev";
var tagsToAdd = new[] { envTag, "dotnet" };
await PutJson($"/objs/{promptObjectId}/versions/{promptDigest}/tags", new
{
    project_id = projectId,
    tags = tagsToAdd,
});
Console.WriteLine($"Tagged:    [{string.Join(", ", tagsToAdd.Select(t => $"\"{t}\""))}] -> version {promptDigest.Substring(0, 12)}…");

// 3) Add named aliases pointing at this version. Aliases are
// per-object_id named pointers — typical examples are deployment
// targets ("staging", "production") and release candidates
// ("v1-candidate"). PUT adds; use POST /objs/{id}/aliases/remove to
// detach an alias. The server also auto-maintains a `latest` alias
// on the most-recent version; do not try to set "latest" yourself.
var aliasesToSet = new[] { "staging", "v1-candidate" };
await PutJson($"/objs/{promptObjectId}/aliases", new
{
    project_id = projectId,
    digest = promptDigest,
    aliases = aliasesToSet,
});
Console.WriteLine($"Aliased:   [{string.Join(", ", aliasesToSet.Select(a => $"\"{a}\""))}] -> version {promptDigest.Substring(0, 12)}…");

// 4) Read it back (with tags + aliases) and assert everything round-trips.
var readBack = await PostJson("/obj/read", new
{
    project_id = projectId,
    object_id = promptObjectId,
    digest = promptDigest,
    include_tags_and_aliases = true,
});
var obj = readBack["obj"]!;

try
{
    var className = obj["val"]!["_class_name"]!.GetValue<string>();
    if (className != "StringPrompt") throw new Exception($"_class_name: {className}");
    var content = obj["val"]!["content"]!.GetValue<string>();
    if (content != promptContent) throw new Exception($"content: {content}");
    var baseClass = obj["base_object_class"]!.GetValue<string>();
    if (baseClass != "Prompt") throw new Exception($"base_object_class: {baseClass}");
    var leafClass = obj["leaf_object_class"]!.GetValue<string>();
    if (leafClass != "StringPrompt") throw new Exception($"leaf_object_class: {leafClass}");
    var versionIndex = obj["version_index"]!.GetValue<int>();

    var tagsList = obj["tags"]?.AsArray().Select(t => t!.GetValue<string>()).ToList() ?? new List<string>();
    var aliasesList = obj["aliases"]?.AsArray().Select(a => a!.GetValue<string>()).ToList() ?? new List<string>();
    foreach (var t in tagsToAdd)
        if (!tagsList.Contains(t))
            throw new Exception($"tag \"{t}\" missing from [{string.Join(", ", tagsList)}]");
    foreach (var a in aliasesToSet)
        if (!aliasesList.Contains(a))
            throw new Exception($"alias \"{a}\" missing from [{string.Join(", ", aliasesList)}]");
    Console.WriteLine($"Read:      version={versionIndex} tags=[{string.Join(", ", tagsList.Select(t => $"\"{t}\""))}] aliases=[{string.Join(", ", aliasesList.Select(a => $"\"{a}\""))}]");

    // 3) Open a Call whose `inputs.prompt` is the Prompt's weave:// ref.
    const string question = "What is the capital of France?";
    var started = await PostJson("/call/start", new
    {
        start = new
        {
            project_id = projectId,
            op_name = "recipe-07-prompt-in-trace",
            started_at = DateTime.UtcNow.ToString("O"),
            attributes = new Dictionary<string, object>
            {
                ["cookbook.language"] = "dotnet",
                ["cookbook.recipe"] = "07_use_prompt",
                ["cookbook.environment"] = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev",
            },
            inputs = new Dictionary<string, object> { ["prompt"] = promptRef, ["question"] = question },
        },
    });
    var callId = started["id"]!.GetValue<string>();
    var traceId = started["trace_id"]!.GetValue<string>();
    Console.WriteLine($"Started:   id={callId} (inputs.prompt = {promptRef})");

    // Client-side: substitute the question into the prompt template.
    var rendered = promptContent.Replace("{question}", question);
    const string answer = "Paris";

    await PostJson("/call/end", new
    {
        end = new
        {
            project_id = projectId,
            id = callId,
            ended_at = DateTime.UtcNow.ToString("O"),
            summary = new { },
            output = new Dictionary<string, object> { ["rendered_prompt"] = rendered, ["answer"] = answer },
        },
    });
    Console.WriteLine($"Ended:     id={callId} output.answer=\"{answer}\"");

    // --- verification ---
    JsonNode? call = null;
    for (var i = 0; i < 5; i++)
    {
        var r = await PostJson("/call/read", new { project_id = projectId, id = callId });
        call = r["call"];
        if (call != null && call["ended_at"]?.GetValue<string?>() != null) break;
        Thread.Sleep(1000);
    }

    if (call is null || call["ended_at"]?.GetValue<string?>() is null)
        throw new Exception($"Call {callId} not visible/finished after 5 reads");

    if (call["inputs"]!["prompt"]!.GetValue<string>() != promptRef)
        throw new Exception($"inputs.prompt: {call["inputs"]!["prompt"]}");
    if (call["inputs"]!["question"]!.GetValue<string>() != question)
        throw new Exception($"inputs.question: {call["inputs"]!["question"]}");
    if (call["output"]!["answer"]!.GetValue<string>() != answer)
        throw new Exception($"output.answer: {call["output"]!["answer"]}");
    if (call["trace_id"]!.GetValue<string>() != traceId)
        throw new Exception($"trace_id: {call["trace_id"]}");

    Console.WriteLine($"Verified:  prompt ref round-trips in call inputs");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
