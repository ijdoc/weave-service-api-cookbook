#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 12: run an evaluation as a 4-level Call trace.
#
# The integration recipe. Looks up everything earlier recipes created,
# builds the structured Call tree the W&B UI recognises as an evaluation
# run, and verifies via /eval_results/query. *Lands ADR-0005* (the
# imperative-SDK-path decision).
#
# The trace shape is what the SDK's `evaluation.evaluate(model)` produces:
#
#     Evaluation.evaluate                            (root, op_name = canonical)
#     +-- Evaluation.predict_and_score              (per-row trial)
#     |   +-- <Model>.predict                        (the model invocation)
#     |   +-- <scorer>                               (scoring)
#     +-- Evaluation.predict_and_score              (row 2)
#     |   +-- ...
#     +-- Evaluation.predict_and_score              (row 3)
#     |   +-- ...
#     +-- Evaluation.summarize                       (sibling of predict_and_score)
#
# What this recipe owns vs what it looks up:
#
# - *Looks up* (created by earlier recipes):
#     - Evaluation Object        -> recipe 11 (extract refs from its val)
#     - canonical Eval Ops       -> recipe 11 (`Evaluation.evaluate`, etc.)
#     - Scorer Op                -> recipe 11's eval val (`scorers[0]`)
#     - Dataset                  -> recipe 11's eval val (`dataset`)
#     - Model + its predict Op   -> recipe 08
# - *Creates*: only Calls. No new Objects or Ops here — recipe 11 owns
#   the eval's definition surface, recipe 12 just executes one run.
#
# Wire-level points worth knowing:
#
# - *Per-Call op_name MUST be a weave:// URI* to an existing Op, not a
#   raw string. The W&B UI's `parseRef` crashes on raw strings.
# - *The root Call's `display_name`* is what the Evaluations UI surfaces
#   as the run's label. Without it, the page falls back to the op_name
#   (`Evaluation.evaluate`) which makes every run look the same. This
#   recipe sets `display_name = "eval-<language>-<unix-epoch>"`.
# - *Root `/call/end` summary* needs `weave.status="success"` and
#   `status_counts.success` = total number of calls in the trace (1 +
#   N x 3 + 1 for N dataset rows). Without these, the UI marks the run
#   as "in progress" or "failed".
# - *The per-row `scores` dict key, and the keys on the aggregated
#   summarize / root output, MUST be the scorer Op's `object_id`* (its
#   short_name in the weave:// ref) — not the scorer's function name
#   and not a generic label like `is_correct`. That's the key the
#   leaderboard view buckets values under across runs; mismatched keys
#   silently drop the row from the leaderboard. The SDK uses the
#   scorer function's name, which happens to equal its `object_id`;
#   the cookbook derives the same string from `scorer_op_ref` since
#   our `object_id` (`recipe-09-is-correct-<lang>`) differs from the
#   scaffold's function name (`is_correct`).
# - *Both `summarize.output` and `root.output` must include a
#   `model_latency.mean` field* alongside the per-scorer aggregate.
#   This too is what the leaderboard reads when rendering the
#   per-run row.
# - *Inputs use raw row values* for simplicity. The SDK uses deep
#   weave:// refs into the Dataset's table rows so the UI can navigate
#   back to the source dataset cell. Both work for /eval_results/query;
#   the cookbook keeps raw values for readability.
# - *The model invocation is mocked* — we pretend the model always
#   returns the expected answer, so `pass_rate` is 1.0. A real recipe
#   would call the LLM named in the Model's `model_name` attribute (see
#   recipe 08) and use the actual response.
#
# Run:
#   ruby ruby/12_run_evaluation.rb

require "json"
require "net/http"
require "time"
require "uri"

BASE_URL = ENV.fetch("WEAVE_SERVICE_URL", "https://trace.wandb.ai")

required = %w[WANDB_API_KEY WANDB_ENTITY WANDB_PROJECT]
missing = required.reject { |k| ENV[k] && !ENV[k].empty? }
abort "Missing required env vars: #{missing.join(", ")}. See ../README.md#setup." unless missing.empty?

ENTITY = ENV.fetch("WANDB_ENTITY")
PROJECT = ENV.fetch("WANDB_PROJECT")
PROJECT_ID = "#{ENTITY}/#{PROJECT}"
API_KEY = ENV.fetch("WANDB_API_KEY")
ENVIRONMENT = ENV.fetch("COOKBOOK_ENVIRONMENT", "dev")

# Fixed per-row latency stub — recipe 12's "model" is a deterministic
# echo, so timing is meaningless. Both the per-row predict_and_score
# output and the aggregated summarize/root output include it because
# that's what the SDK emits and what the UI's aggregator expects to
# average across rows.
MODEL_LATENCY = 0.001


def request_json(method_class, path, body)
  uri = URI.join(BASE_URL, path)
  req = method_class.new(uri, "Content-Type" => "application/json")
  req.basic_auth("api", API_KEY)
  req.body = JSON.dump(body) if body
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for #{path}: #{res.body}" unless res.code.start_with?("2")
  res.body.empty? ? {} : JSON.parse(res.body)
end

def post_json(path, body)
  request_json(Net::HTTP::Post, path, body)
end

def get_json(path)
  uri = URI.join(BASE_URL, path)
  req = Net::HTTP::Get.new(uri, "Content-Type" => "application/json")
  req.basic_auth("api", API_KEY)
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for #{path}: #{res.body}" unless res.code.start_with?("2")
  res.body.empty? ? {} : JSON.parse(res.body)
end

def now
  Time.now.utc.iso8601(6)
end

def latest_object(object_id)
  body = post_json("/objs/query", {
    "project_id" => PROJECT_ID,
    "filter" => { "object_ids" => [object_id], "latest_only" => true },
    "metadata_only" => false,
  })
  body["objs"]&.first
end

def start_call(op_name, inputs, parent_id: nil, trace_id: nil, display_name: nil)
  payload = {
    "project_id" => PROJECT_ID,
    "op_name" => op_name,
    "started_at" => now,
    "attributes" => {
      "cookbook.language" => "ruby",
      "cookbook.recipe" => "12_run_evaluation",
      "cookbook.environment" => ENVIRONMENT,
    },
    "inputs" => inputs,
  }
  payload["parent_id"] = parent_id if parent_id
  payload["trace_id"] = trace_id if trace_id
  payload["display_name"] = display_name if display_name
  r = post_json("/call/start", { "start" => payload })
  [r["id"], r["trace_id"]]
end

def end_call(call_id, output)
  post_json("/call/end", {
    "end" => {
      "project_id" => PROJECT_ID,
      "id" => call_id,
      "ended_at" => now,
      "summary" => {
        "status_counts" => { "success" => 1, "error" => 0 },
        "weave" => { "status" => "success" },
      },
      "output" => output,
    },
  })
end


# 1) Look up the Evaluation Object + extract refs from its val.
# Recipe 11's val carries the canonical Op refs + dataset + scorer.
eval_obj = latest_object("recipe-11-eval-ruby")
abort "FAIL: Evaluation Object `recipe-11-eval-ruby` not found. Run ruby/11_create_evaluation.rb first." if eval_obj.nil?
eval_obj_ref = "weave:///#{PROJECT_ID}/object/#{eval_obj["object_id"]}:#{eval_obj["digest"]}"
ev = eval_obj["val"]
evaluate_op_ref = ev["evaluate"]
predict_and_score_op_ref = ev["predict_and_score"]
summarize_op_ref = ev["summarize"]
scorer_op_ref = ev["scorers"][0]
dataset_ref = ev["dataset"]
# The scorer Op's short_name (object_id) is the key the leaderboard
# aggregator uses to bucket per-row scores. Compute once; reuse for
# the per-row `scores` dict, the wandb.runnable.* feedback_type, and
# the summarize + root output keys.
scorer_short_name = scorer_op_ref.rpartition("/op/").last.partition(":").first
puts "Eval obj:  #{eval_obj["object_id"]} digest=#{eval_obj["digest"][0, 12]}…"


# 2) Look up the Model + its predict Op (recipe 08).
model_obj = latest_object("recipe-08-model-ruby")
abort "FAIL: Model `recipe-08-model-ruby` not found. Run ruby/08_use_model.rb first." if model_obj.nil?
model_ref = "weave:///#{PROJECT_ID}/object/#{model_obj["object_id"]}:#{model_obj["digest"]}"

model_predict_op = latest_object("recipe-08-model-ruby.predict")
abort "FAIL: Model predict Op `recipe-08-model-ruby.predict` not found. Run ruby/08_use_model.rb first." if model_predict_op.nil?
model_predict_op_ref = "weave:///#{PROJECT_ID}/op/#{model_predict_op["object_id"]}:#{model_predict_op["digest"]}"
puts "Model:     #{model_obj["object_id"]} digest=#{model_obj["digest"][0, 12]}…"


# 3) Walk the Dataset rows. dataset_ref is a weave:// URI; the v2 read
# returns a `rows` field that's another ref into a Table; /table/query
# yields the actual row data.
m = dataset_ref.match(%r{weave:///[^/]+/[^/]+/object/([^:]+):(.+)})
abort "FAIL: could not parse dataset_ref: #{dataset_ref.inspect}" if m.nil?
ds_id = m[1]
ds_digest = m[2]
ds_meta = get_json("/v2/#{ENTITY}/#{PROJECT}/datasets/#{ds_id}/versions/#{ds_digest}")
rows_ref = ds_meta["rows"]
table_digest = rows_ref[%r{/table/([A-Za-z0-9_-]+)$}, 1] || rows_ref
rows_res = post_json("/table/query", { "project_id" => PROJECT_ID, "digest" => table_digest })
rows = rows_res["rows"].map { |row| row["val"] }
puts "Dataset:   #{ds_id} (#{rows.length} rows)"


# 4) Build the 4-level Call trace. The display_name on the root is the
# Evaluations-page label; without it the page shows the bare op_name.
display_name = "eval-ruby-#{Time.now.to_i}"
root_id, trace_id = start_call(
  evaluate_op_ref,
  { "self" => eval_obj_ref, "model" => model_ref },
  display_name: display_name,
)
puts "Root call: #{root_id} (display_name=#{display_name.inspect})"

n_pass = 0
total_calls = 1 # root
rows.each do |row|
  ps_id, = start_call(
    predict_and_score_op_ref,
    { "self" => eval_obj_ref, "model" => model_ref, "example" => row },
    parent_id: root_id, trace_id: trace_id,
  )

  # Predict child: invoke the (mocked) model.
  pred_id, = start_call(
    model_predict_op_ref,
    { "self" => model_ref, "question" => row["question"] },
    parent_id: ps_id, trace_id: trace_id,
  )
  # Mock: pretend the model always returns the expected answer.
  # A real recipe would call the LLM named in the Model's `model_name`
  # attribute (recipe 08) and use its response here.
  prediction = row["answer"]
  end_call(pred_id, { "answer" => prediction })

  # Scorer child: compare prediction vs expected.
  sc_id, = start_call(
    scorer_op_ref,
    { "output" => prediction, "expected" => row["answer"] },
    parent_id: ps_id, trace_id: trace_id,
  )
  score = prediction == row["answer"]
  end_call(sc_id, score)

  # Link the score to the predict Call via a `wandb.runnable.*`
  # Feedback row — same pattern as recipe 09's apply_scorer. The
  # SDK adds this on every per-row predict during eval.evaluate();
  # without it, the score shows in the per-row output but there's
  # no scorer-Op attribution at the leaderboard level (cross-model
  # comparison views key off these Feedback rows). Recipe 12 has to
  # post them explicitly because we're driving the trace directly.
  pred_call_ref = "weave:///#{PROJECT_ID}/call/#{pred_id}"
  score_call_ref = "weave:///#{PROJECT_ID}/call/#{sc_id}"
  post_json("/feedback/create", {
    "project_id" => PROJECT_ID,
    "weave_ref" => pred_call_ref,
    "feedback_type" => "wandb.runnable.#{scorer_short_name}",
    "payload" => { "output" => score },
    "runnable_ref" => scorer_op_ref,
    "call_ref" => score_call_ref,
  })

  # End predict_and_score with the per-row aggregated output. The SDK
  # includes a model_latency value here too.
  #
  # CRITICAL: the key in `scores` MUST be the scorer Op's short name
  # (its `object_id`) — same string used in the wandb.runnable.*
  # feedback_type above. This is what links the per-row scorer_key
  # in /eval_results/query's response back to the Eval Object's
  # val.scorers list, which is what powers the UI's scorer-object
  # attribution and the cross-model leaderboard view.
  end_call(
    ps_id,
    { "output" => prediction, "scores" => { scorer_short_name => score }, "model_latency" => MODEL_LATENCY },
  )

  n_pass += 1 if score
  total_calls += 3 # predict_and_score + predict + scorer
end

# Summarize: sibling of predict_and_score under the root. Carries the
# aggregated scorer stats.
sum_id, = start_call(
  summarize_op_ref,
  { "self" => eval_obj_ref },
  parent_id: root_id, trace_id: trace_id,
)
pass_rate = rows.empty? ? 0.0 : n_pass.to_f / rows.length
# Both summarize.output AND root.output must be keyed by the scorer's
# short_name (matching val.scorers[i] and the per-row scorer_key) and
# carry a `model_latency.mean` field. This dict IS what the leaderboard
# view reads: it buckets values across runs by these top-level keys to
# render the cross-model comparison table. A key that doesn't match
# val.scorers — or a missing model_latency aggregate — and the row
# silently drops out of the leaderboard.
aggregated_output = {
  scorer_short_name => { "true_count" => n_pass, "true_fraction" => pass_rate },
  "model_latency" => { "mean" => MODEL_LATENCY },
}
end_call(sum_id, aggregated_output)
total_calls += 1 # summarize


# 5) End the root with the proper summary shape — status_counts.success
# is the total call count; weave.status="success" + display_name make
# the UI render the run as finished.
post_json("/call/end", {
  "end" => {
    "project_id" => PROJECT_ID,
    "id" => root_id,
    "ended_at" => now,
    "summary" => {
      "status_counts" => { "success" => total_calls, "error" => 0 },
      "weave" => { "status" => "success", "display_name" => display_name },
    },
    "output" => aggregated_output,
  },
})
puts "Trace done: #{total_calls} calls, pass_rate=#{format("%.2f", pass_rate)}"


# --- verification ---
# /eval_results/query with the root call_id aggregates per-row trial
# data + scorer stats. The summary's evaluation_ref should match the
# Eval Object we ran against.
sleep 2
results = nil
last = nil
8.times do
  last = post_json("/v2/#{ENTITY}/#{PROJECT}/eval_results/query", {
    "evaluation_call_ids" => [root_id],
    "include_rows" => true,
    "include_summary" => true,
  })
  if last["total_rows"] == rows.length
    results = last
    break
  end
  sleep 1
end
abort "FAIL: eval_results/query did not return #{rows.length} rows after 8 attempts (last=#{last && last["total_rows"].inspect})" if results.nil?

evals = results["summary"]["evaluations"]
abort "expected 1 evaluation in summary, got #{evals.length}" unless evals.length == 1
ev_summary = evals[0]
abort "evaluation_ref: #{ev_summary["evaluation_ref"].inspect}" unless ev_summary["evaluation_ref"] == eval_obj_ref
scorer_keys = ev_summary["scorer_stats"].map { |s| s["scorer_key"] }
abort "#{scorer_short_name.inspect} missing from scorer_stats: #{scorer_keys.inspect}" unless scorer_keys.include?(scorer_short_name)
puts "Verified:  /eval_results/query returned #{results["total_rows"]} rows, evaluation_ref matches, scorer_stats=#{scorer_keys.inspect}"
