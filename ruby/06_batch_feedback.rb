#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 06: attach feedback to many Calls in one request.
#
# Demonstrates the bulk variant of /feedback/create:
#   POST /feedback/batch/create  -> N feedback items in one round trip
#
# Two wire-level points worth knowing:
#
# - The path is `/feedback/batch/create`, not the more guessable
#   `/feedback/create-batch` or `/feedback/createBatch`.
# - The body wraps a parallel-indexed array under `batch`:
#       {"batch" => [<FeedbackCreateReq>, <FeedbackCreateReq>, ...]}
#   Each item carries its own `project_id`, `weave_ref`, `feedback_type`,
#   and `payload` — exactly the shape /feedback/create takes. The
#   response mirrors the input with {"res" => [<FeedbackCreateRes>, ...]},
#   indices aligned to the input batch.
#
# When to reach for batch over the per-item endpoint:
#
# - Bulk-annotate a list of Calls after a review pass (this recipe's
#   shape — one note per Call).
# - Dump multiple feedback items at the end of a turn (scorer outputs,
#   then notes, then ...).
# - Anywhere round-trip count matters (many small items, latency-bound
#   uploader).
#
# This recipe creates three Calls and attaches *two feedback items per
# Call* in a single batch request: a `wandb.note.1` (UI-visible in the
# trace table) and a custom scorer-style feedback (queryable via
# /feedback/query but not surfaced in the trace table). One round trip
# ships 6 items; the same shape via per-item /feedback/create would
# require 6 round trips.
#
# This mirrors recipe 05's note + scorer split — same pair, but bulk.
#
# Run:
#   ruby ruby/06_batch_feedback.rb

require "json"
require "net/http"
require "time"
require "uri"

BASE_URL = ENV.fetch("WEAVE_SERVICE_URL", "https://trace.wandb.ai")

required = %w[WANDB_API_KEY WANDB_ENTITY WANDB_PROJECT]
missing = required.reject { |k| ENV[k] && !ENV[k].empty? }
abort "Missing required env vars: #{missing.join(", ")}. See ../README.md#setup." unless missing.empty?

PROJECT_ID = "#{ENV.fetch("WANDB_ENTITY")}/#{ENV.fetch("WANDB_PROJECT")}"
API_KEY = ENV.fetch("WANDB_API_KEY")

BASE_ATTRIBUTES = {
  "cookbook.language" => "ruby",
  "cookbook.recipe" => "06_batch_feedback",
  "cookbook.environment" => ENV.fetch("COOKBOOK_ENVIRONMENT", "dev"),
}.freeze
NOTE_TYPE = "wandb.note.1"
SCORER_TYPE = "recipe-06-scorer-correctness"

def post_json(path, body)
  uri = URI.join(BASE_URL, path)
  req = Net::HTTP::Post.new(uri, "Content-Type" => "application/json")
  req.basic_auth("api", API_KEY)
  req.body = JSON.dump(body)
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for #{path}: #{res.body}" unless res.code.start_with?("2")
  res.body.empty? ? {} : JSON.parse(res.body)
end

def start_call(op_name, inputs)
  res = post_json("/call/start", {
    "start" => {
      "project_id" => PROJECT_ID,
      "op_name" => op_name,
      "started_at" => Time.now.utc.iso8601,
      "attributes" => BASE_ATTRIBUTES,
      "inputs" => inputs,
    },
  })
  res.fetch("id")
end

def end_call(call_id, output)
  post_json("/call/end", {
    "end" => {
      "project_id" => PROJECT_ID,
      "id" => call_id,
      "ended_at" => Time.now.utc.iso8601,
      "summary" => {},
      "output" => output,
    },
  })
end

# Create three Calls — same shape as recipe 01, just repeated.
questions = [
  ["What is the capital of France?", "Paris"],
  ["What is the capital of Spain?", "Madrid"],
  ["What is the capital of Italy?", "Rome"],
]
calls = questions.each_with_index.map do |(question, answer), i|
  num = i + 1
  call_id = start_call("recipe-06-call-#{num}", { "question" => question })
  end_call(call_id, { "answer" => answer })
  puts "Call #{num}: id=#{call_id}"
  {
    "id" => call_id,
    "ref" => "weave:///#{PROJECT_ID}/call/#{call_id}",
    "answer" => answer,
  }
end

# Build the batch — note + scorer feedback per Call (6 items total).
batch = calls.flat_map do |call|
  [
    {
      "project_id" => PROJECT_ID,
      "weave_ref" => call["ref"],
      "feedback_type" => NOTE_TYPE,
      "payload" => { "note" => "Reviewed — answer: '#{call["answer"]}'" },
    },
    {
      "project_id" => PROJECT_ID,
      "weave_ref" => call["ref"],
      "feedback_type" => SCORER_TYPE,
      "payload" => { "output" => { "score" => 1.0, "reason" => "Answer '#{call["answer"]}' matches expected" } },
    },
  ]
end

# Single round trip for all three items.
res = post_json("/feedback/batch/create", { "batch" => batch })
results = res.fetch("res")
abort "batch size mismatch: sent #{batch.size} got #{results.size}" unless results.size == batch.size
batch.zip(results).each do |item, r|
  puts "Batch->Feedback: type=#{item["feedback_type"]} feedback_id=#{r["id"]}"
end

# --- verification ---
# For each Call, query feedback by weave_ref and assert both the note
# and the scorer feedback landed with the expected payload. Brief retry
# tolerates eventual consistency in the read path.
expected_types = [NOTE_TYPE, SCORER_TYPE]
calls.each do |call|
  expected_note = { "note" => "Reviewed — answer: '#{call["answer"]}'" }
  expected_scorer = { "output" => { "score" => 1.0, "reason" => "Answer '#{call["answer"]}' matches expected" } }
  by_type = {}
  5.times do
    body = post_json("/feedback/query", {
      "project_id" => PROJECT_ID,
      "query" => {
        "$expr" => {
          "$eq" => [
            { "$getField" => "weave_ref" },
            { "$literal" => call["ref"] },
          ],
        },
      },
    })
    rows = body.fetch("result", [])
    by_type = rows.each_with_object({}) do |r, h|
      h[r["feedback_type"]] = r if expected_types.include?(r["feedback_type"])
    end
    break if (expected_types - by_type.keys).empty?

    sleep 1
  end
  abort "FAIL: feedback for #{call["ref"]} not all visible after 5 reads (got #{by_type.keys.inspect})" unless (expected_types - by_type.keys).empty?
  abort "note payload for #{call["id"]}: #{by_type[NOTE_TYPE]["payload"].inspect}" unless by_type[NOTE_TYPE]["payload"] == expected_note
  abort "scorer payload for #{call["id"]}: #{by_type[SCORER_TYPE]["payload"].inspect}" unless by_type[SCORER_TYPE]["payload"] == expected_scorer
  by_type.each_value do |row|
    abort "weave_ref drift: #{row["weave_ref"].inspect}" unless row["weave_ref"] == call["ref"]
  end
end

puts "Verified: #{batch.size} batched feedback items across #{calls.size} Calls (note + scorer each)"
