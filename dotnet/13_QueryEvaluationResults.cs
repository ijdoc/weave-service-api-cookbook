// Recipe 13: query evaluation results.
//
// The "look at what already ran" recipe. Recipe 12 builds an evaluation
// run; recipe 13 aggregates across runs and walks the per-trial data —
// exactly what the W&B UI's *Evaluations* leaderboard view does.
//
// Two endpoint patterns combined:
//
// 1. *`/calls/stream_query`* with `filter.op_names = [val.evaluate]` and
//    `filter.trace_roots_only = true` — finds every root Call using the
//    canonical `Evaluation.evaluate` Op. Returns NDJSON: one Call object
//    per line.
// 2. *`/v2/{entity}/{project}/eval_results/query`* with
//    `evaluation_call_ids = [<list of root call ids>]` — server-side
//    aggregator that pulls each run's predict_and_score / scorer
//    children, computes per-scorer stats per run, and (with
//    `include_rows=true`) returns a row-major view of trial data so you
//    can compare the same dataset row across runs.
//
// What this recipe owns vs what it looks up:
//
// - *Looks up* (created by earlier recipes):
//     - Evaluation Object        -> recipe 11 (extract `val.evaluate` for
//                                   the op_names filter)
//     - One or more eval runs    -> recipe 12
// - *Creates*: nothing. Pure read-only.
//
// Wire-level points worth knowing:
//
// - *Filter by op_names with a full weave:// ref*, not just the short
//   name. `op_names = [evaluateOpRef]` returns all root Calls bound
//   to that exact Op version. Because the canonical Eval Ops are
//   content-addressed and stable across runs, this is enough to find
//   every run that used this eval definition's evaluate Op.
// - *Filter by Eval Object client-side*. The canonical
//   `Evaluation.evaluate` Op is *shared across Eval Objects of the
//   same shape*; `op_names` alone returns runs across multiple Eval
//   Objects. Narrow with `inputs.self.StartsWith(evalObjPrefix)` —
//   the prefix matches any version of our Eval Object's `object_id`.
// - *`summary.evaluations[]` is one entry per *run*, not per Eval
//   Object version. Each carries `evaluation_call_id`, `evaluation_ref`,
//   `model_ref`, `display_name`, `started_at`, `trial_count`, and a
//   `scorer_stats[]` array with rich aggregates (`pass_rate`,
//   `pass_true_count`, `numeric_mean`, ...).
// - *`rows[]` is row-major*. Each entry is keyed by the dataset row's
//   content hash (`row_digest`), with a nested `evaluations[]` array
//   whose `trials[]` give per-run, per-trial output + scores. So the
//   same dataset row across multiple runs lives in one `rows[]` entry —
//   that's what powers per-row cross-run comparison in the UI.
//
// Run:
//   dotnet run dotnet/13_QueryEvaluationResults.cs

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

const string evalObjectId = "recipe-11-eval-dotnet";

using var http = new HttpClient();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Basic",
    Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{apiKey}")));

async Task<JsonNode> PostJson(string path, object body)
{
    var json = JsonSerializer.Serialize(body, jsonOptions);
    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
    using var res = await http.SendAsync(req);
    var responseBody = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        throw new HttpRequestException($"HTTP {(int)res.StatusCode} for {path}: {responseBody}");
    return string.IsNullOrEmpty(responseBody) ? new JsonObject() : JsonNode.Parse(responseBody)!;
}

async Task<List<JsonNode>> PostNdjson(string path, object body)
{
    // /calls/stream_query streams one JSON object per line, not a single
    // JSON document. JsonNode.Parse on the raw body fails on the second
    // line.
    var json = JsonSerializer.Serialize(body, jsonOptions);
    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
    using var res = await http.SendAsync(req);
    var responseBody = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        throw new HttpRequestException($"HTTP {(int)res.StatusCode} for {path}: {responseBody}");
    var lines = responseBody.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    return lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonNode.Parse(l)!).ToList();
}

async Task<JsonNode?> LatestObject(string objectId)
{
    var body = await PostJson("/objs/query", new
    {
        project_id = projectId,
        filter = new { object_ids = new[] { objectId }, latest_only = true },
        metadata_only = false,
    });
    var arr = body["objs"]?.AsArray();
    if (arr == null || arr.Count == 0) return null;
    return arr[0];
}


// 1) Look up the Eval Object (recipe 11). We need `val.evaluate` — the
// canonical Op ref — to scope the run search.
var evalObj = await LatestObject(evalObjectId);
if (evalObj is null)
{
    Console.Error.WriteLine($"FAIL: Evaluation Object `{evalObjectId}` not found. Run dotnet/11_CreateEvaluation.cs first.");
    return 1;
}
var evaluateOpRef = evalObj["val"]!["evaluate"]!.GetValue<string>();
var evalObjPrefix = $"weave:///{projectId}/object/{evalObjectId}:";
Console.WriteLine($"Eval obj:   {evalObjectId} (latest digest={evalObj["digest"]!.GetValue<string>().Substring(0, 12)}…)");
Console.WriteLine($"Op filter:  {evaluateOpRef}");


// 2) Find every root Call using this Evaluation.evaluate Op, then
// narrow to runs against our Eval Object (any version) by matching
// `inputs.self` against the object_id prefix.
//
// Retry loop: /calls/stream_query is eventually-consistent. A run
// finished by recipe 12 a moment ago might not be indexed yet, and
// in a brand-new project this would race recipe 13 to zero results.
// Sleep + retry until at least one matching run shows up.
var runs = new List<JsonNode>();
for (var i = 0; i < 8; i++)
{
    var roots = await PostNdjson("/calls/stream_query", new
    {
        project_id = projectId,
        filter = new { trace_roots_only = true, op_names = new[] { evaluateOpRef } },
        limit = 50,
        sort_by = new[] { new { field = "started_at", direction = "desc" } },
    });
    runs = roots.Where(c => (c["inputs"]?["self"]?.GetValue<string?>() ?? "").StartsWith(evalObjPrefix)).ToList();
    if (runs.Count > 0) break;
    Thread.Sleep(1000);
}
if (runs.Count == 0)
{
    Console.Error.WriteLine($"FAIL: no eval runs against `{evalObjectId}` found after 8 reads. Run dotnet/12_RunEvaluation.cs first.");
    return 1;
}
Console.WriteLine($"Found:      {runs.Count} run(s) against `{evalObjectId}` (any version)");


// 3) Aggregate across all of them via /eval_results/query. The server
// pulls each run's predict_and_score + scorer children, computes
// per-scorer stats per run, and (with include_rows) returns a
// row-major trial view.
var res = await PostJson($"/v2/{entity}/{project}/eval_results/query", new
{
    evaluation_call_ids = runs.Select(c => c["id"]!.GetValue<string>()).ToArray(),
    include_rows = true,
    include_summary = true,
});
var totalRows = res["total_rows"]!.GetValue<int>();
var evaluations = res["summary"]!["evaluations"]!.AsArray();
Console.WriteLine($"Aggregated: total_rows={totalRows}, evaluations in summary={evaluations.Count}");
Console.WriteLine();


// 4) Print the per-run leaderboard view: one line per run with the
// scorer aggregates the UI's Evaluations page shows.
Console.WriteLine("RUNS (newest first):");
Console.WriteLine($"  {"display_name",-32}  {"started_at",-20}  {"trials",6}  scorer summary");
foreach (var ev in evaluations)
{
    var scorerStats = ev!["scorer_stats"]?.AsArray() ?? new JsonArray();
    var scorerSummary = string.Join(", ", scorerStats.Select(s =>
    {
        var key = s!["scorer_key"]!.GetValue<string>();
        var passTrue = s["pass_true_count"]!.GetValue<int>();
        var passKnown = s["pass_known_count"]!.GetValue<int>();
        var passRate = s["pass_rate"]!.GetValue<double>();
        return $"{key}={passTrue}/{passKnown} (pass_rate={passRate:F2})";
    }));
    var startedRaw = ev["started_at"]?.GetValue<string?>() ?? "";
    var started = startedRaw.Length >= 19 ? startedRaw.Substring(0, 19) : startedRaw;
    var displayName = ev["display_name"]?.GetValue<string?>() ?? "?";
    var trialCount = ev["trial_count"]!.GetValue<int>();
    Console.WriteLine($"  {displayName,-32}  {started,-20}  {trialCount,6}  {scorerSummary}");
}


// 5) Per-row drill-down: walk the first row's evaluations to show how
// the same dataset row was answered across runs. This is what the UI's
// "compare across runs" view consumes.
Console.WriteLine("\nROW 0 across all runs:");
var rows = res["rows"]!.AsArray();
var row0 = rows[0]!;
Console.WriteLine($"  row_digest={row0["row_digest"]!.GetValue<string>().Substring(0, 16)}…");
foreach (var runBlock in row0["evaluations"]!.AsArray())
{
    var callId = runBlock!["evaluation_call_id"]!.GetValue<string>();
    var runLabel = evaluations.FirstOrDefault(ev => ev!["evaluation_call_id"]!.GetValue<string>() == callId)
        ?["display_name"]?.GetValue<string?>() ?? "?";
    foreach (var trial in runBlock["trials"]!.AsArray())
    {
        var modelOutput = trial!["model_output"]?.ToJsonString() ?? "null";
        var scoresNode = trial["scores"];
        var scoresStr = "";
        if (scoresNode is JsonObject scoresObj)
            scoresStr = string.Join(", ", scoresObj.Select(kv => $"{kv.Key}={kv.Value?.ToJsonString()}"));
        Console.WriteLine($"  - run={runLabel,-32} output={modelOutput,-10} scores={{{scoresStr}}}");
    }
}


// --- verification ---
// All three load-bearing fields populated:
// - at least one run
// - per-run scorer_stats with the expected scorer key
// - per-row trial data
try
{
    if (totalRows <= 0)
        throw new Exception($"expected total_rows > 0, got {totalRows}");
    if (evaluations.Count == 0)
        throw new Exception("no evaluations in summary");
    var scorerKeysSeen = evaluations
        .SelectMany(ev => (ev!["scorer_stats"]?.AsArray() ?? new JsonArray())
            .Select(s => s!["scorer_key"]!.GetValue<string>()))
        .Distinct()
        .ToList();
    var scorerOpRef = evalObj["val"]!["scorers"]!.AsArray()[0]!.GetValue<string>();
    var expectedScorerKey = scorerOpRef.Substring(scorerOpRef.LastIndexOf("/op/") + "/op/".Length).Split(':')[0];
    if (!scorerKeysSeen.Contains(expectedScorerKey))
        throw new Exception($"scorer key \"{expectedScorerKey}\" missing from [{string.Join(", ", scorerKeysSeen.OrderBy(x => x).Select(s => $"\"{s}\""))}] — " +
                            "did recipe 12 use the canonical scorer-Op object_id as the scores-dict key?");
    if (rows.Count == 0)
        throw new Exception("expected rows[] populated (include_rows=true)");
    if ((row0["evaluations"]?.AsArray()?.Count ?? 0) == 0)
        throw new Exception("row 0 has no nested evaluations");
    Console.WriteLine($"\nVerified:   {totalRows} trials across {evaluations.Count} run(s); scorer_keys=[{string.Join(", ", scorerKeysSeen.OrderBy(x => x).Select(s => $"\"{s}\""))}]");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
