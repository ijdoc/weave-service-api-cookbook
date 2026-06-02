// Recipe 12: run an evaluation as a 4-level Call trace.
//
// The integration recipe. Looks up everything earlier recipes created,
// builds the structured Call tree the W&B UI recognises as an evaluation
// run, and verifies via /eval_results/query. *Lands ADR-0005* (the
// imperative-SDK-path decision).
//
// The trace shape is what the SDK's `evaluation.evaluate(model)` produces:
//
//     Evaluation.evaluate                            (root, op_name = canonical)
//     +-- Evaluation.predict_and_score              (per-row trial)
//     |   +-- <Model>.predict                        (the model invocation)
//     |   +-- <scorer>                               (scoring)
//     +-- Evaluation.predict_and_score              (row 2)
//     |   +-- ...
//     +-- Evaluation.predict_and_score              (row 3)
//     |   +-- ...
//     +-- Evaluation.summarize                       (sibling of predict_and_score)
//
// What this recipe owns vs what it looks up:
//
// - *Looks up* (created by earlier recipes):
//     - Evaluation Object        -> recipe 11 (extract refs from its val)
//     - canonical Eval Ops       -> recipe 11 (`Evaluation.evaluate`, etc.)
//     - Scorer Op                -> recipe 11's eval val (`scorers[0]`)
//     - Dataset                  -> recipe 11's eval val (`dataset`)
//     - Model + its predict Op   -> recipe 08
// - *Creates*: only Calls. No new Objects or Ops here — recipe 11 owns
//   the eval's definition surface, recipe 12 just executes one run.
//
// Wire-level points worth knowing:
//
// - *Per-Call op_name MUST be a weave:// URI* to an existing Op, not a
//   raw string. The W&B UI's `parseRef` crashes on raw strings.
// - *The root Call's `display_name`* is what the Evaluations UI surfaces
//   as the run's label. Without it, the page falls back to the op_name
//   (`Evaluation.evaluate`) which makes every run look the same. This
//   recipe sets `display_name = "eval-<language>-<unix-epoch>"`.
// - *Root `/call/end` summary* needs `weave.status="success"` and
//   `status_counts.success` = total number of calls in the trace (1 +
//   N x 3 + 1 for N dataset rows). Without these, the UI marks the run
//   as "in progress" or "failed".
// - *The per-row `scores` dict key, and the keys on the aggregated
//   summarize / root output, MUST be the scorer Op's `object_id`* (its
//   short_name in the weave:// ref) — not the scorer's function name
//   and not a generic label like `is_correct`. That's the key the
//   leaderboard view buckets values under across runs; mismatched keys
//   silently drop the row from the leaderboard. The SDK uses the
//   scorer function's name, which happens to equal its `object_id`;
//   the cookbook derives the same string from `scorerOpRef` since
//   our `object_id` (`recipe-09-is-correct-<lang>`) differs from the
//   scaffold's function name (`is_correct`).
// - *Both `summarize.output` and `root.output` must include a
//   `model_latency.mean` field* alongside the per-scorer aggregate.
//   This too is what the leaderboard reads when rendering the
//   per-run row.
// - *Inputs use raw row values* for simplicity. The SDK uses deep
//   weave:// refs into the Dataset's table rows so the UI can navigate
//   back to the source dataset cell. Both work for /eval_results/query;
//   the cookbook keeps raw values for readability.
// - *The model invocation is mocked* — we pretend the model always
//   returns the expected answer, so `pass_rate` is 1.0. A real recipe
//   would call the LLM named in the Model's `model_name` attribute (see
//   recipe 08) and use the actual response.
//
// Run:
//   dotnet run dotnet/12_RunEvaluation.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
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
string environment = Environment.GetEnvironmentVariable("COOKBOOK_ENVIRONMENT") ?? "dev";

// Fixed per-row latency stub — recipe 12's "model" is a deterministic
// echo, so timing is meaningless. Both the per-row predict_and_score
// output and the aggregated summarize/root output include it because
// that's what the SDK emits and what the UI's aggregator expects to
// average across rows.
const double ModelLatency = 0.001;

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

async Task<JsonNode> GetJson(string path)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
    using var res = await http.SendAsync(req);
    var responseBody = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        throw new HttpRequestException($"HTTP {(int)res.StatusCode} for {path}: {responseBody}");
    return string.IsNullOrEmpty(responseBody) ? new JsonObject() : JsonNode.Parse(responseBody)!;
}

string Now() => DateTime.UtcNow.ToString("O");

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

async Task<(string id, string traceId)> StartCall(string opName, object inputs, string? parentId = null,
    string? traceId = null, string? displayName = null)
{
    var payload = new Dictionary<string, object?>
    {
        ["project_id"] = projectId,
        ["op_name"] = opName,
        ["started_at"] = Now(),
        ["attributes"] = new Dictionary<string, object>
        {
            ["cookbook.language"] = "dotnet",
            ["cookbook.recipe"] = "12_run_evaluation",
            ["cookbook.environment"] = environment,
        },
        ["inputs"] = inputs,
    };
    if (parentId != null) payload["parent_id"] = parentId;
    if (traceId != null) payload["trace_id"] = traceId;
    if (displayName != null) payload["display_name"] = displayName;
    var r = await PostJson("/call/start", new { start = payload });
    return (r["id"]!.GetValue<string>(), r["trace_id"]!.GetValue<string>());
}

async Task EndCall(string callId, object? output)
{
    await PostJson("/call/end", new
    {
        end = new
        {
            project_id = projectId,
            id = callId,
            ended_at = Now(),
            summary = new
            {
                status_counts = new { success = 1, error = 0 },
                weave = new { status = "success" },
            },
            output,
        },
    });
}


// 1) Look up the Evaluation Object + extract refs from its val.
// Recipe 11's val carries the canonical Op refs + dataset + scorer.
var evalObj = await LatestObject("recipe-11-eval-dotnet");
if (evalObj is null)
{
    Console.Error.WriteLine("FAIL: Evaluation Object `recipe-11-eval-dotnet` not found. Run dotnet/11_CreateEvaluation.cs first.");
    return 1;
}
var evalObjectIdStr = evalObj["object_id"]!.GetValue<string>();
var evalDigestStr = evalObj["digest"]!.GetValue<string>();
var evalObjRef = $"weave:///{projectId}/object/{evalObjectIdStr}:{evalDigestStr}";
var ev = evalObj["val"]!;
var evaluateOpRef = ev["evaluate"]!.GetValue<string>();
var predictAndScoreOpRef = ev["predict_and_score"]!.GetValue<string>();
var summarizeOpRef = ev["summarize"]!.GetValue<string>();
var scorerOpRef = ev["scorers"]!.AsArray()[0]!.GetValue<string>();
var datasetRef = ev["dataset"]!.GetValue<string>();
// The scorer Op's short_name (object_id) is the key the leaderboard
// aggregator uses to bucket per-row scores. Compute once; reuse for
// the per-row `scores` dict, the wandb.runnable.* feedback_type, and
// the summarize + root output keys.
var scorerShortName = scorerOpRef.Substring(scorerOpRef.LastIndexOf("/op/") + "/op/".Length).Split(':')[0];
Console.WriteLine($"Eval obj:  {evalObjectIdStr} digest={evalDigestStr.Substring(0, 12)}…");


// 2) Look up the Model + its predict Op (recipe 08).
var modelObj = await LatestObject("recipe-08-model-dotnet");
if (modelObj is null)
{
    Console.Error.WriteLine("FAIL: Model `recipe-08-model-dotnet` not found. Run dotnet/08_UseModel.cs first.");
    return 1;
}
var modelRef = $"weave:///{projectId}/object/{modelObj["object_id"]!.GetValue<string>()}:{modelObj["digest"]!.GetValue<string>()}";

var modelPredictOp = await LatestObject("recipe-08-model-dotnet.predict");
if (modelPredictOp is null)
{
    Console.Error.WriteLine("FAIL: Model predict Op `recipe-08-model-dotnet.predict` not found. Run dotnet/08_UseModel.cs first.");
    return 1;
}
var modelPredictOpRef = $"weave:///{projectId}/op/{modelPredictOp["object_id"]!.GetValue<string>()}:{modelPredictOp["digest"]!.GetValue<string>()}";
Console.WriteLine($"Model:     {modelObj["object_id"]} digest={modelObj["digest"]!.GetValue<string>().Substring(0, 12)}…");


// 3) Walk the Dataset rows. datasetRef is a weave:// URI; the v2 read
// returns a `rows` field that's another ref into a Table; /table/query
// yields the actual row data.
var dsMatch = Regex.Match(datasetRef, @"weave:///[^/]+/[^/]+/object/([^:]+):(.+)");
if (!dsMatch.Success)
{
    Console.Error.WriteLine($"FAIL: could not parse datasetRef: {datasetRef}");
    return 1;
}
var dsId = dsMatch.Groups[1].Value;
var dsDigest = dsMatch.Groups[2].Value;
var dsMeta = await GetJson($"/v2/{entity}/{project}/datasets/{dsId}/versions/{dsDigest}");
var rowsRef = dsMeta["rows"]!.GetValue<string>();
var tableMatch = Regex.Match(rowsRef, @"/table/([A-Za-z0-9_-]+)$");
var tableDigest = tableMatch.Success ? tableMatch.Groups[1].Value : rowsRef;
var rowsRes = await PostJson("/table/query", new { project_id = projectId, digest = tableDigest });
var rows = rowsRes["rows"]!.AsArray().Select(r => r!["val"]!).ToList();
Console.WriteLine($"Dataset:   {dsId} ({rows.Count} rows)");


// 4) Build the 4-level Call trace. The display_name on the root is the
// Evaluations-page label; without it the page shows the bare op_name.
var displayName = $"eval-dotnet-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
var (rootId, traceId) = await StartCall(
    evaluateOpRef,
    new Dictionary<string, object?> { ["self"] = evalObjRef, ["model"] = modelRef },
    displayName: displayName);
Console.WriteLine($"Root call: {rootId} (display_name=\"{displayName}\")");

var nPass = 0;
var totalCalls = 1; // root
foreach (var rowNode in rows)
{
    var row = rowNode.AsObject();
    var question = row["question"]!.GetValue<string>();
    var expected = row["answer"]!.GetValue<string>();

    var (psId, _) = await StartCall(
        predictAndScoreOpRef,
        new Dictionary<string, object?>
        {
            ["self"] = evalObjRef,
            ["model"] = modelRef,
            ["example"] = new Dictionary<string, object> { ["question"] = question, ["answer"] = expected },
        },
        parentId: rootId, traceId: traceId);

    // Predict child: invoke the (mocked) model.
    var (predId, _) = await StartCall(
        modelPredictOpRef,
        new Dictionary<string, object?> { ["self"] = modelRef, ["question"] = question },
        parentId: psId, traceId: traceId);
    // Mock: pretend the model always returns the expected answer.
    // A real recipe would call the LLM named in the Model's `model_name`
    // attribute (recipe 08) and use its response here.
    var prediction = expected;
    await EndCall(predId, new Dictionary<string, object> { ["answer"] = prediction });

    // Scorer child: compare prediction vs expected.
    var (scId, _) = await StartCall(
        scorerOpRef,
        new Dictionary<string, object?> { ["output"] = prediction, ["expected"] = expected },
        parentId: psId, traceId: traceId);
    var score = prediction == expected;
    await EndCall(scId, score);

    // Link the score to the predict Call via a `wandb.runnable.*`
    // Feedback row — same pattern as recipe 09's apply_scorer. The
    // SDK adds this on every per-row predict during eval.evaluate();
    // without it, the score shows in the per-row output but there's
    // no scorer-Op attribution at the leaderboard level (cross-model
    // comparison views key off these Feedback rows). Recipe 12 has to
    // post them explicitly because we're driving the trace directly.
    var predCallRef = $"weave:///{projectId}/call/{predId}";
    var scoreCallRef = $"weave:///{projectId}/call/{scId}";
    await PostJson("/feedback/create", new
    {
        project_id = projectId,
        weave_ref = predCallRef,
        feedback_type = $"wandb.runnable.{scorerShortName}",
        payload = new Dictionary<string, object> { ["output"] = score },
        runnable_ref = scorerOpRef,
        call_ref = scoreCallRef,
    });

    // End predict_and_score with the per-row aggregated output. The SDK
    // includes a model_latency value here too.
    //
    // CRITICAL: the key in `scores` MUST be the scorer Op's short name
    // (its `object_id`) — same string used in the wandb.runnable.*
    // feedback_type above. This is what links the per-row scorer_key
    // in /eval_results/query's response back to the Eval Object's
    // val.scorers list, which is what powers the UI's scorer-object
    // attribution and the cross-model leaderboard view.
    await EndCall(psId, new Dictionary<string, object>
    {
        ["output"] = prediction,
        ["scores"] = new Dictionary<string, object> { [scorerShortName] = score },
        ["model_latency"] = ModelLatency,
    });

    if (score) nPass++;
    totalCalls += 3; // predict_and_score + predict + scorer
}

// Summarize: sibling of predict_and_score under the root. Carries the
// aggregated scorer stats.
var (sumId, _) = await StartCall(
    summarizeOpRef,
    new Dictionary<string, object?> { ["self"] = evalObjRef },
    parentId: rootId, traceId: traceId);
var passRate = rows.Count == 0 ? 0.0 : (double)nPass / rows.Count;
// Both summarize.output AND root.output must be keyed by the scorer's
// short_name (matching val.scorers[i] and the per-row scorer_key) and
// carry a `model_latency.mean` field. This dict IS what the leaderboard
// view reads: it buckets values across runs by these top-level keys to
// render the cross-model comparison table. A key that doesn't match
// val.scorers — or a missing model_latency aggregate — and the row
// silently drops out of the leaderboard.
var aggregatedOutput = new Dictionary<string, object>
{
    [scorerShortName] = new Dictionary<string, object> { ["true_count"] = nPass, ["true_fraction"] = passRate },
    ["model_latency"] = new Dictionary<string, object> { ["mean"] = ModelLatency },
};
await EndCall(sumId, aggregatedOutput);
totalCalls++; // summarize


// 5) End the root with the proper summary shape — status_counts.success
// is the total call count; weave.status="success" + display_name make
// the UI render the run as finished.
await PostJson("/call/end", new
{
    end = new
    {
        project_id = projectId,
        id = rootId,
        ended_at = Now(),
        summary = new
        {
            status_counts = new { success = totalCalls, error = 0 },
            weave = new { status = "success", display_name = displayName },
        },
        output = aggregatedOutput,
    },
});
Console.WriteLine($"Trace done: {totalCalls} calls, pass_rate={passRate:F2}");


// --- verification ---
// /eval_results/query with the root call_id aggregates per-row trial
// data + scorer stats. The summary's evaluation_ref should match the
// Eval Object we ran against.
Thread.Sleep(2000);
JsonNode? results = null;
JsonNode? last = null;
for (var i = 0; i < 8; i++)
{
    last = await PostJson($"/v2/{entity}/{project}/eval_results/query", new
    {
        evaluation_call_ids = new[] { rootId },
        include_rows = true,
        include_summary = true,
    });
    if (last["total_rows"]?.GetValue<int>() == rows.Count)
    {
        results = last;
        break;
    }
    Thread.Sleep(1000);
}
if (results is null)
{
    Console.Error.WriteLine($"FAIL: eval_results/query did not return {rows.Count} rows after 8 attempts (last={last?["total_rows"]?.ToJsonString() ?? "null"})");
    return 1;
}

try
{
    var evals = results["summary"]!["evaluations"]!.AsArray();
    if (evals.Count != 1)
        throw new Exception($"expected 1 evaluation in summary, got {evals.Count}");
    var evSummary = evals[0]!;
    var evRefSeen = evSummary["evaluation_ref"]!.GetValue<string>();
    if (evRefSeen != evalObjRef)
        throw new Exception($"evaluation_ref: {evRefSeen}");
    var scorerKeys = evSummary["scorer_stats"]!.AsArray().Select(s => s!["scorer_key"]!.GetValue<string>()).ToList();
    if (!scorerKeys.Contains(scorerShortName))
        throw new Exception($"\"{scorerShortName}\" missing from scorer_stats: [{string.Join(", ", scorerKeys.Select(s => $"\"{s}\""))}]");
    Console.WriteLine($"Verified:  /eval_results/query returned {results["total_rows"]} rows, evaluation_ref matches, scorer_stats=[{string.Join(", ", scorerKeys.Select(s => $"\"{s}\""))}]");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 1;
}
