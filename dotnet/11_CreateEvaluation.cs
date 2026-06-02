// Recipe 11: set up an Evaluation Object.
//
// Pulls everything from earlier recipes together into a single Evaluation
// *definition* — the versioned, content-addressed Object that recipe 12
// will execute and recipe 13 will query against. After this recipe runs,
// the W&B UI's *Evaluation Definitions* page (`/weave/evaluation-definitions`)
// shows it as a browsable definition with no associated runs yet.
//
// The recipe builds two kinds of artifacts:
//
// 1. *Three canonical Eval Ops* (`Evaluation.evaluate`,
//    `Evaluation.predict_and_score`, `Evaluation.summarize`) — inert
//    lifecycle-marker Ops registered via a two-step
//    `/file/create` + `/obj/create` flow with ADR-0004 scaffolds.
//    The W&B service identifies these Ops by their `object_id` and
//    uses them to recognise an evaluation Call trace
//    (`/eval_results/query` filters on the exact canonical names,
//    case-sensitive). The source is a stub `raise NotImplementedError`;
//    the real eval logic lives in recipe 12 client-side.
//    Content-addressed — re-running an unchanged recipe 11 is a no-op;
//    editing this recipe bumps the Op versions (and downstream the
//    Eval Object version too).
//
// 2. *The Evaluation Object itself* — built via `POST /obj/create`
//    with `builtin_object_class="Evaluation"`, referencing the freshly
//    registered canonical Ops + the recipe-08 Model + the recipe-09
//    Scorer Op + the recipe-10 Dataset, all by weave:// URI.
//
// Recipe 12 (Run an evaluation) will look up the canonical Eval Ops and
// the Eval Object created here; recipe 13 (Query results) does the same.
// *Don't duplicate the scaffolds in recipes 12 / 13* — they live here
// only, so editing the eval's definition is a single-file change and
// the Eval Object version bumps atomically with the scaffold edits.
//
// Wire-level points worth knowing:
//
// - *`/obj/create` with `builtin_object_class="Evaluation"`* is the
//   cookbook's chosen path (matching the SDK). The specialized
//   `POST /v2/.../evaluations` endpoint also exists but auto-creates
//   per-eval-aliased Ops (`<eval-id>.evaluate`) the cookbook doesn't
//   use — `/eval_results/query` filters by canonical name, not
//   per-eval-aliased name. ADR-0005 (lands with recipe 12) captures
//   this decision in detail.
// - *Why `/file/create` + `/obj/create` for the Ops, not `/v2/.../ops`?*
//   The `/v2/.../ops` endpoint lowercases `object_id`
//   (`Evaluation.evaluate` -> `evaluation.evaluate`) — and
//   `/eval_results/query` filters on the exact capital-case names.
//   The SDK uses `/file/create` (multipart) to upload the source and
//   `/obj/create` to wrap it as a `kind="op"` Object — that path
//   preserves case. The cookbook follows suit. The Op's val mirrors
//   the SDK shape:
//       {"_type": "CustomWeaveType",
//        "files": {"obj.py": "<file digest>"},
//        "weave_type": {"type": "Op"}}
// - The Eval Object val mirrors the SDK shape: `_bases=["Object",
//   "BaseModel"]`, `_class_name="Evaluation"`, `_type="Evaluation"`,
//   plus the field refs (dataset, evaluate, predict_and_score,
//   summarize, scorers, trials). Per-language identity comes from a
//   per-language `object_id` (`recipe-11-eval-<lang>`); canonical Op
//   names stay shared because the aggregator's filter requires it.
// - Tags + aliases (recipe 07's pattern) apply here too — tagging the
//   Eval Object with environment / language gives UI-visible labels on
//   the Evaluation Definitions page.
//
// Run:
//   dotnet run dotnet/11_CreateEvaluation.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

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

const string recipePath = "dotnet/11_CreateEvaluation.cs";
string recipeAbsPath = Path.Combine(Directory.GetCurrentDirectory(), recipePath);
byte[] recipeBytes = File.ReadAllBytes(recipeAbsPath);
string recipeSha;
using (var sha = SHA256.Create())
{
    recipeSha = Convert.ToHexString(sha.ComputeHash(recipeBytes)).ToLowerInvariant().Substring(0, 16);
}

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
    using var res = await http.SendAsync(req);
    var responseBody = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        throw new HttpRequestException($"HTTP {(int)res.StatusCode} for {path}: {responseBody}");
    return string.IsNullOrEmpty(responseBody) ? new JsonObject() : JsonNode.Parse(responseBody)!;
}

Task<JsonNode> PostJson(string path, object body) => SendJson(HttpMethod.Post, path, body);
Task<JsonNode> PutJson(string path, object body) => SendJson(HttpMethod.Put, path, body);

async Task<string> UploadOpSource(string source)
{
    // Upload Op source as a file (multipart) and return the file digest.
    // /file/create is the ONE multipart endpoint the cookbook uses;
    // every other endpoint takes JSON. The returned digest goes into
    // the Op's val under `files.obj.py`.
    using var form = new MultipartFormDataContent();
    form.Add(new StringContent(projectId), "project_id");
    var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(source));
    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    form.Add(fileContent, "file", "obj.py");
    using var res = await http.PostAsync(baseUrl + "/file/create", form);
    var body = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        throw new HttpRequestException($"HTTP {(int)res.StatusCode} for /file/create: {body}");
    return JsonNode.Parse(body)!["digest"]!.GetValue<string>();
}

async Task<JsonNode?> LatestObject(string objectId)
{
    var body = await PostJson("/objs/query", new
    {
        project_id = projectId,
        filter = new { object_ids = new[] { objectId }, latest_only = true },
        metadata_only = true,
    });
    var arr = body["objs"]?.AsArray();
    if (arr == null || arr.Count == 0) return null;
    return arr[0];
}

async Task<JsonNode?> LatestDatasetByPrefix(string prefix)
{
    // Recipe 10 timestamps Dataset names so exact lookup won't work —
    // list Datasets sorted desc by created_at and pick the first
    // prefix match.
    var body = await PostJson("/objs/query", new
    {
        project_id = projectId,
        filter = new { base_object_classes = new[] { "Dataset" } },
        sort_by = new[] { new { field = "created_at", direction = "desc" } },
        limit = 50,
        metadata_only = true,
    });
    var arr = body["objs"]?.AsArray();
    if (arr == null) return null;
    foreach (var o in arr)
    {
        if (o?["object_id"]?.GetValue<string?>() is string oid && oid.StartsWith(prefix))
            return o;
    }
    return null;
}


// 1) Look up the prerequisites from earlier recipes. Abort with a clear
// pointer to the recipe that would create the missing artifact.
var model = await LatestObject("recipe-08-model-dotnet");
if (model is null)
{
    Console.Error.WriteLine("FAIL: model `recipe-08-model-dotnet` not found. Run dotnet/08_UseModel.cs first.");
    return 1;
}
Console.WriteLine($"Found:     model    {model["object_id"]} digest={model["digest"]!.GetValue<string>().Substring(0, 12)}…");

var scorer = await LatestObject("recipe-09-is-correct-dotnet");
if (scorer is null)
{
    Console.Error.WriteLine("FAIL: scorer `recipe-09-is-correct-dotnet` not found. Run dotnet/09_ScoreACall.cs first.");
    return 1;
}
Console.WriteLine($"Found:     scorer   {scorer["object_id"]} digest={scorer["digest"]!.GetValue<string>().Substring(0, 12)}…");

var dataset = await LatestDatasetByPrefix("recipe-10-dataset-dotnet");
if (dataset is null)
{
    Console.Error.WriteLine("FAIL: no Dataset matching `recipe-10-dataset-dotnet-*` found. Run dotnet/10_CreateDataset.cs first.");
    return 1;
}
Console.WriteLine($"Found:     dataset  {dataset["object_id"]} digest={dataset["digest"]!.GetValue<string>().Substring(0, 12)}…");


// 2) Register the three canonical Eval Ops with ADR-0004 scaffolds.
// Content-addressed: re-running an unchanged recipe is a no-op (same
// digest stays); editing this recipe bumps version_index and (in
// step 3) bumps the Eval Object too.
//
// C# 11 raw-string-with-interpolation: the outer ${"""" delimiter (four
// quotes) lets us include literal triple-quote (""") for the Python
// docstring without escaping.
string Scaffold(string opName, string signature, string bodyDoc) => $""""
# Cookbook scaffold (dotnet)
# Source: {recipePath}
# SHA256: {recipeSha}

import weave


@weave.op
def {signature}:
    """{bodyDoc}

    Byte-for-byte reference (SHA256 of the recipe file):
        {recipeSha}

    To verify a local copy of the file matches (POSIX shell):
        shasum -a 256 {recipePath} | cut -c1-16

    Canonical lifecycle-marker Op for the cookbook's eval flow. The
    W&B service identifies this Op by `object_id` ({opName}) and uses
    it to recognise the structured Call trace recipe 12 builds. The
    body raises NotImplementedError by design — real eval logic lives
    client-side in recipe 12.
    """
    raise NotImplementedError(
        "This op is a Python scaffold uploaded from a non-Python recipe. "
        "See the docstring above for the real source-language file and a "
        "verifiable byte-for-byte reference (SHA256)."
    )
"""";

var canonicalOps = new Dictionary<string, string>
{
    ["Evaluation.evaluate"] = Scaffold(
        "Evaluation.evaluate",
        "evaluate(self, model)",
        "Root of an evaluation Call trace. Wraps one full pass over\n    the dataset with the given model + scorers."),
    ["Evaluation.predict_and_score"] = Scaffold(
        "Evaluation.predict_and_score",
        "predict_and_score(self, example)",
        "Per-row child of the eval root. One trial = one dataset row\n    scored by all configured scorers."),
    ["Evaluation.summarize"] = Scaffold(
        "Evaluation.summarize",
        "summarize(self, eval_table)",
        "Final sibling of predict_and_score children under the root.\n    Aggregates per-row scorer outputs into evaluation-level stats."),
};

var evalOpRefs = new Dictionary<string, string>();
foreach (var (opId, source) in canonicalOps)
{
    var fileDigest = await UploadOpSource(source);
    var res = await PostJson("/obj/create", new
    {
        obj = new
        {
            project_id = projectId,
            object_id = opId,
            val = new Dictionary<string, object>
            {
                ["_type"] = "CustomWeaveType",
                ["files"] = new Dictionary<string, object> { ["obj.py"] = fileDigest },
                ["weave_type"] = new Dictionary<string, object> { ["type"] = "Op" },
            },
        },
    });
    evalOpRefs[opId] = $"weave:///{projectId}/op/{res["object_id"]!.GetValue<string>()}:{res["digest"]!.GetValue<string>()}";
    Console.WriteLine($"Op:        {res["object_id"]} digest={res["digest"]!.GetValue<string>().Substring(0, 12)}… (file={fileDigest.Substring(0, 12)}…)");
}


// 3) Build the Evaluation Object. The val mirrors the SDK shape: each
// canonical Op is a structured `method` field on the object (so the
// W&B UI can render them inline on the Eval Definitions page), and
// `scorers` is a list of Op refs.
string ObjRefOf(JsonNode o) => $"weave:///{projectId}/object/{o["object_id"]!.GetValue<string>()}:{o["digest"]!.GetValue<string>()}";
string OpRefOf(JsonNode o) => $"weave:///{projectId}/op/{o["object_id"]!.GetValue<string>()}:{o["digest"]!.GetValue<string>()}";

const string evalObjectId = "recipe-11-eval-dotnet";
var evalVal = new Dictionary<string, object?>
{
    ["_bases"] = new[] { "Object", "BaseModel" },
    ["_class_name"] = "Evaluation",
    ["_type"] = "Evaluation",
    ["name"] = evalObjectId,
    ["description"] = "Cookbook evaluation definition (dotnet recipe 11)",
    ["dataset"] = ObjRefOf(dataset),
    ["evaluate"] = evalOpRefs["Evaluation.evaluate"],
    ["predict_and_score"] = evalOpRefs["Evaluation.predict_and_score"],
    ["summarize"] = evalOpRefs["Evaluation.summarize"],
    ["scorers"] = new[] { OpRefOf(scorer) },
    ["trials"] = 1,
    ["evaluation_name"] = null,
    ["metadata"] = null,
    ["preprocess_model_input"] = null,
};
var created = await PostJson("/obj/create", new
{
    obj = new
    {
        project_id = projectId,
        object_id = evalObjectId,
        val = evalVal,
        builtin_object_class = "Evaluation",
    },
});
var evalDigest = created["digest"]!.GetValue<string>();
var evalRef = $"weave:///{projectId}/object/{evalObjectId}:{evalDigest}";
Console.WriteLine($"Published: {evalObjectId} digest={evalDigest.Substring(0, 12)}…");
Console.WriteLine($"  ref: {evalRef}");


// 4) Tag + alias (recipe 07's pattern). Tags are per-version, additive,
// UI-visible labels; aliases are per-object_id named pointers.
var envTag = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev";
var tagsToAdd = new[] { envTag, "dotnet" };
await PutJson($"/objs/{evalObjectId}/versions/{evalDigest}/tags", new
{
    project_id = projectId,
    tags = tagsToAdd,
});
Console.WriteLine($"Tagged:    [{string.Join(", ", tagsToAdd.Select(t => $"\"{t}\""))}] -> version {evalDigest.Substring(0, 12)}…");

var aliasesToSet = new[] { "staging" };
await PutJson($"/objs/{evalObjectId}/aliases", new
{
    project_id = projectId,
    digest = evalDigest,
    aliases = aliasesToSet,
});
Console.WriteLine($"Aliased:   [{string.Join(", ", aliasesToSet.Select(a => $"\"{a}\""))}] -> version {evalDigest.Substring(0, 12)}…");


// --- verification ---
// Read the Eval Object back (with tags + aliases) and assert every ref
// + metadata field round-trips. Brief retry for read-after-write lag.
JsonNode? readBack = null;
for (var i = 0; i < 8; i++)
{
    var r = await PostJson("/obj/read", new
    {
        project_id = projectId,
        object_id = evalObjectId,
        digest = evalDigest,
        include_tags_and_aliases = true,
    });
    readBack = r["obj"];
    // Retry until the obj is visible AND tags + aliases have propagated.
    // /obj/create returns synchronously but tags / aliases land via a
    // separate propagation path; reading the obj before they catch up
    // is racy.
    if (readBack != null)
    {
        var tagsNow = readBack["tags"]?.AsArray().Select(t => t!.GetValue<string>()).ToList() ?? new List<string>();
        var aliasesNow = readBack["aliases"]?.AsArray().Select(a => a!.GetValue<string>()).ToList() ?? new List<string>();
        if (tagsToAdd.All(t => tagsNow.Contains(t)) && aliasesToSet.All(a => aliasesNow.Contains(a))) break;
    }
    Thread.Sleep(1000);
}

try
{
    if (readBack is null)
        throw new Exception($"Eval Object {evalObjectId}:{evalDigest} not visible after 5 reads");

    var val = readBack["val"]!;
    if (val["_class_name"]!.GetValue<string>() != "Evaluation")
        throw new Exception($"_class_name: {val["_class_name"]}");
    if (val["dataset"]!.GetValue<string>() != ObjRefOf(dataset))
        throw new Exception($"dataset: {val["dataset"]}");
    if (val["evaluate"]!.GetValue<string>() != evalOpRefs["Evaluation.evaluate"])
        throw new Exception($"evaluate: {val["evaluate"]}");
    if (val["predict_and_score"]!.GetValue<string>() != evalOpRefs["Evaluation.predict_and_score"])
        throw new Exception($"predict_and_score: {val["predict_and_score"]}");
    if (val["summarize"]!.GetValue<string>() != evalOpRefs["Evaluation.summarize"])
        throw new Exception($"summarize: {val["summarize"]}");
    var scorersArr = val["scorers"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray();
    if (scorersArr.Length != 1 || scorersArr[0] != OpRefOf(scorer))
        throw new Exception($"scorers: {val["scorers"]}");
    if (val["trials"]!.GetValue<int>() != 1)
        throw new Exception($"trials: {val["trials"]}");
    if (readBack["base_object_class"]!.GetValue<string>() != "Evaluation")
        throw new Exception($"base_object_class: {readBack["base_object_class"]}");

    var tagsList = readBack["tags"]?.AsArray().Select(t => t!.GetValue<string>()).ToList() ?? new List<string>();
    var aliasesList = readBack["aliases"]?.AsArray().Select(a => a!.GetValue<string>()).ToList() ?? new List<string>();
    foreach (var t in tagsToAdd)
        if (!tagsList.Contains(t))
            throw new Exception($"tag \"{t}\" missing from [{string.Join(", ", tagsList)}]");
    foreach (var a in aliasesToSet)
        if (!aliasesList.Contains(a))
            throw new Exception($"alias \"{a}\" missing from [{string.Join(", ", aliasesList)}]");
    Console.WriteLine($"Verified:  Eval Object refs + tags + aliases round-trip (tags=[{string.Join(", ", tagsList.Select(t => $"\"{t}\""))}], aliases=[{string.Join(", ", aliasesList.Select(a => $"\"{a}\""))}])");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
