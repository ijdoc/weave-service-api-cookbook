#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 13: query evaluation results.
#
# The "look at what already ran" recipe. Recipe 12 builds an evaluation
# run; recipe 13 aggregates across runs and walks the per-trial data —
# exactly what the W&B UI's *Evaluations* leaderboard view does.
#
# Two endpoint patterns combined:
#
# 1. *`/calls/stream_query`* with `filter.op_names = [val.evaluate]` and
#    `filter.trace_roots_only = true` — finds every root Call using the
#    canonical `Evaluation.evaluate` Op. Returns NDJSON: one Call object
#    per line.
# 2. *`/v2/{entity}/{project}/eval_results/query`* with
#    `evaluation_call_ids = [<list of root call ids>]` — server-side
#    aggregator that pulls each run's predict_and_score / scorer
#    children, computes per-scorer stats per run, and (with
#    `include_rows=true`) returns a row-major view of trial data so you
#    can compare the same dataset row across runs.
#
# What this recipe owns vs what it looks up:
#
# - *Looks up* (created by earlier recipes):
#     - Evaluation Object        -> recipe 11 (extract `val.evaluate` for
#                                   the op_names filter)
#     - One or more eval runs    -> recipe 12
# - *Creates*: nothing. Pure read-only.
#
# Wire-level points worth knowing:
#
# - *Filter by op_names with a full weave:// ref*, not just the short
#   name. `op_names = [evaluate_op_ref]` returns all root Calls bound
#   to that exact Op version. Because the canonical Eval Ops are
#   content-addressed and stable across runs, this is enough to find
#   every run that used this eval definition's evaluate Op.
# - *Filter by Eval Object client-side*. The canonical
#   `Evaluation.evaluate` Op is *shared across Eval Objects of the
#   same shape*; `op_names` alone returns runs across multiple Eval
#   Objects. Narrow with `inputs.self.start_with?(eval_obj_prefix)` —
#   the prefix matches any version of our Eval Object's `object_id`.
# - *`summary.evaluations[]` is one entry per *run*, not per Eval
#   Object version. Each carries `evaluation_call_id`, `evaluation_ref`,
#   `model_ref`, `display_name`, `started_at`, `trial_count`, and a
#   `scorer_stats[]` array with rich aggregates (`pass_rate`,
#   `pass_true_count`, `numeric_mean`, ...).
# - *`rows[]` is row-major*. Each entry is keyed by the dataset row's
#   content hash (`row_digest`), with a nested `evaluations[]` array
#   whose `trials[]` give per-run, per-trial output + scores. So the
#   same dataset row across multiple runs lives in one `rows[]` entry —
#   that's what powers per-row cross-run comparison in the UI.
#
# Run:
#   ruby ruby/13_query_evaluation_results.rb

require "json"
require "net/http"
require "uri"

BASE_URL = ENV.fetch("WEAVE_SERVICE_URL", "https://trace.wandb.ai")

required = %w[WANDB_API_KEY WANDB_ENTITY WANDB_PROJECT]
missing = required.reject { |k| ENV[k] && !ENV[k].empty? }
abort "Missing required env vars: #{missing.join(", ")}. See ../README.md#setup." unless missing.empty?

ENTITY = ENV.fetch("WANDB_ENTITY")
PROJECT = ENV.fetch("WANDB_PROJECT")
PROJECT_ID = "#{ENTITY}/#{PROJECT}"
API_KEY = ENV.fetch("WANDB_API_KEY")

EVAL_OBJECT_ID = "recipe-11-eval-ruby"


def post_json(path, body)
  uri = URI.join(BASE_URL, path)
  req = Net::HTTP::Post.new(uri, "Content-Type" => "application/json")
  req.basic_auth("api", API_KEY)
  req.body = JSON.dump(body)
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for #{path}: #{res.body}" unless res.code.start_with?("2")
  res.body.empty? ? {} : JSON.parse(res.body)
end

def post_ndjson(path, body)
  # /calls/stream_query streams one JSON object per line, not a single
  # JSON document. Parsing the raw body with JSON.parse raises on the
  # second line.
  uri = URI.join(BASE_URL, path)
  req = Net::HTTP::Post.new(uri, "Content-Type" => "application/json")
  req.basic_auth("api", API_KEY)
  req.body = JSON.dump(body)
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for #{path}: #{res.body}" unless res.code.start_with?("2")
  res.body.each_line.map(&:strip).reject(&:empty?).map { |l| JSON.parse(l) }
end

def latest_object(object_id)
  body = post_json("/objs/query", {
    "project_id" => PROJECT_ID,
    "filter" => { "object_ids" => [object_id], "latest_only" => true },
    "metadata_only" => false,
  })
  body["objs"]&.first
end


# 1) Look up the Eval Object (recipe 11). We need `val.evaluate` — the
# canonical Op ref — to scope the run search.
eval_obj = latest_object(EVAL_OBJECT_ID)
abort "FAIL: Evaluation Object `#{EVAL_OBJECT_ID}` not found. Run ruby/11_create_evaluation.rb first." if eval_obj.nil?
evaluate_op_ref = eval_obj["val"]["evaluate"]
eval_obj_prefix = "weave:///#{PROJECT_ID}/object/#{EVAL_OBJECT_ID}:"
puts "Eval obj:   #{EVAL_OBJECT_ID} (latest digest=#{eval_obj["digest"][0, 12]}…)"
puts "Op filter:  #{evaluate_op_ref}"


# 2) Find every root Call using this Evaluation.evaluate Op, then
# narrow to runs against our Eval Object (any version) by matching
# `inputs.self` against the object_id prefix.
#
# Retry loop: /calls/stream_query is eventually-consistent. A run
# finished by recipe 12 a moment ago might not be indexed yet, and
# in a brand-new project this would race recipe 13 to zero results.
# Sleep + retry until at least one matching run shows up.
runs = []
8.times do
  roots = post_ndjson("/calls/stream_query", {
    "project_id" => PROJECT_ID,
    "filter" => { "trace_roots_only" => true, "op_names" => [evaluate_op_ref] },
    "limit" => 50,
    "sort_by" => [{ "field" => "started_at", "direction" => "desc" }],
  })
  runs = roots.select { |c| ((c["inputs"] || {})["self"] || "").start_with?(eval_obj_prefix) }
  break unless runs.empty?

  sleep 1
end
abort "FAIL: no eval runs against `#{EVAL_OBJECT_ID}` found after 8 reads. Run ruby/12_run_evaluation.rb first." if runs.empty?
puts "Found:      #{runs.length} run(s) against `#{EVAL_OBJECT_ID}` (any version)"


# 3) Aggregate across all of them via /eval_results/query. The server
# pulls each run's predict_and_score + scorer children, computes
# per-scorer stats per run, and (with include_rows) returns a
# row-major trial view.
res = post_json("/v2/#{ENTITY}/#{PROJECT}/eval_results/query", {
  "evaluation_call_ids" => runs.map { |c| c["id"] },
  "include_rows" => true,
  "include_summary" => true,
})
total_rows = res["total_rows"]
evaluations = res["summary"]["evaluations"]
puts "Aggregated: total_rows=#{total_rows}, evaluations in summary=#{evaluations.length}"
puts


# 4) Print the per-run leaderboard view: one line per run with the
# scorer aggregates the UI's Evaluations page shows.
puts "RUNS (newest first):"
puts format("  %-32s  %-20s  %6s  %s", "display_name", "started_at", "trials", "scorer summary")
evaluations.each do |ev|
  scorer_summary = (ev["scorer_stats"] || []).map do |s|
    "#{s["scorer_key"]}=#{s["pass_true_count"]}/#{s["pass_known_count"]} (pass_rate=#{format("%.2f", s["pass_rate"])})"
  end.join(", ")
  started = (ev["started_at"] || "")[0, 19]
  puts format("  %-32s  %-20s  %6d  %s", ev["display_name"] || "?", started, ev["trial_count"], scorer_summary)
end


# 5) Per-row drill-down: walk the first row's evaluations to show how
# the same dataset row was answered across runs. This is what the UI's
# "compare across runs" view consumes.
puts "\nROW 0 across all runs:"
row0 = res["rows"][0]
puts "  row_digest=#{row0["row_digest"][0, 16]}…"
row0["evaluations"].each do |run_block|
  call_id = run_block["evaluation_call_id"]
  run_label = evaluations.find { |ev| ev["evaluation_call_id"] == call_id }&.dig("display_name") || "?"
  run_block["trials"].each do |trial|
    scores_str = (trial["scores"] || {}).map { |k, v| "#{k}=#{v}" }.join(", ")
    puts format("  - run=%-32s output=%-10s scores={%s}", run_label, trial["model_output"].inspect, scores_str)
  end
end


# --- verification ---
# All three load-bearing fields populated:
# - at least one run
# - per-run scorer_stats with the expected scorer key
# - per-row trial data
abort "expected total_rows > 0, got #{total_rows}" unless total_rows > 0
abort "no evaluations in summary" if evaluations.empty?
scorer_keys_seen = evaluations.flat_map { |ev| (ev["scorer_stats"] || []).map { |s| s["scorer_key"] } }.uniq
expected_scorer_key = eval_obj["val"]["scorers"][0].rpartition("/op/").last.partition(":").first
unless scorer_keys_seen.include?(expected_scorer_key)
  abort "scorer key #{expected_scorer_key.inspect} missing from #{scorer_keys_seen.sort.inspect} — " \
        "did recipe 12 use the canonical scorer-Op object_id as the scores-dict key?"
end
abort "expected rows[] populated (include_rows=true)" if res["rows"].nil? || res["rows"].empty?
abort "row 0 has no nested evaluations" if (row0["evaluations"] || []).empty?
puts "\nVerified:   #{total_rows} trials across #{evaluations.length} run(s); scorer_keys=#{scorer_keys_seen.sort.inspect}"
