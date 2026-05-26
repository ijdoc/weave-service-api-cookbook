#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 05: attach feedback to a Call.
#
# Demonstrates the feedback lifecycle:
#   POST /feedback/create  -> attach feedback to a Call
#   POST /feedback/query   -> read it back
#
# Three wire-level points worth knowing:
#
# - The Call is identified by `weave_ref`, not `call_id` directly:
#       weave:///{entity}/{project}/call/{call_id}
#   The recipe constructs this URI inline. There is also a `call_ref`
#   field, but `weave_ref` is the required one.
# - /feedback/create body is *flat* — top-level `project_id`,
#   `weave_ref`, `feedback_type`, `payload` (no wrapper key, like
#   /call/update; unlike /call/start and /call/end).
# - /feedback/query uses the typed Query language. Filtering by
#   `weave_ref` looks like:
#       {"$expr" => {"$eq" => [
#         {"$getField" => "weave_ref"},
#         {"$literal" => "weave:///..."}
#       ]}}
#
# `feedback_type` is a freeform string. By convention:
# - `wandb.<kind>.<version>` is reserved for W&B-recognized types that
#   get UI treatment (e.g., `wandb.note.1`, `wandb.reaction.1`).
# - Scorer-emitted feedback typically uses the scorer's name as a prefix
#   so it's distinguishable from human annotation.
#
# This recipe attaches one of each to the same Call.
#
# Run:
#   ruby ruby/05_add_feedback.rb

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

OP_NAME = "recipe-05-add-feedback"
ATTRIBUTES = {
  "cookbook.language" => "ruby",
  "cookbook.recipe" => "05_add_feedback",
  "cookbook.environment" => ENV.fetch("COOKBOOK_ENVIRONMENT", "dev"),
}.freeze
INPUTS = { "question" => "What is the capital of Germany?" }.freeze
OUTPUT = { "answer" => "Berlin" }.freeze

HUMAN_NOTE = {
  "feedback_type" => "wandb.note.1",
  "payload" => { "note" => "Answer looks correct." },
}.freeze
SCORER_FEEDBACK = {
  "feedback_type" => "recipe-05-scorer-correctness",
  "payload" => { "output" => { "score" => 1.0, "reason" => "Answer matches expected" } },
}.freeze

def post_json(path, body)
  uri = URI.join(BASE_URL, path)
  req = Net::HTTP::Post.new(uri, "Content-Type" => "application/json")
  req.basic_auth("api", API_KEY)
  req.body = JSON.dump(body)
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for #{path}: #{res.body}" unless res.code.start_with?("2")
  res.body.empty? ? {} : JSON.parse(res.body)
end

# Open the Call.
started = post_json("/call/start", {
  "start" => {
    "project_id" => PROJECT_ID,
    "op_name" => OP_NAME,
    "started_at" => Time.now.utc.iso8601,
    "attributes" => ATTRIBUTES,
    "inputs" => INPUTS,
  },
})
call_id = started.fetch("id")
puts "Started: id=#{call_id}"

# Close it.
post_json("/call/end", {
  "end" => {
    "project_id" => PROJECT_ID,
    "id" => call_id,
    "ended_at" => Time.now.utc.iso8601,
    "summary" => {},
    "output" => OUTPUT,
  },
})
puts "Ended:   id=#{call_id}"

# Build the Call's weave_ref. /feedback/create takes this URI string,
# not a raw call_id.
call_ref = "weave:///#{PROJECT_ID}/call/#{call_id}"

# Attach both feedback items.
[HUMAN_NOTE, SCORER_FEEDBACK].each do |fb|
  res = post_json("/feedback/create", {
    "project_id" => PROJECT_ID,
    "weave_ref" => call_ref,
    "feedback_type" => fb["feedback_type"],
    "payload" => fb["payload"],
  })
  puts "Feedback: id=#{res.fetch("id")} type=#{fb["feedback_type"]}"
end

# --- verification ---
# Query feedback filtered to this Call by weave_ref, asserting both
# items land with the expected feedback_type + payload. Brief retry
# tolerates eventual consistency in the read path.
expected_types = [HUMAN_NOTE["feedback_type"], SCORER_FEEDBACK["feedback_type"]].sort
rows = []
5.times do
  res = post_json("/feedback/query", {
    "project_id" => PROJECT_ID,
    "query" => {
      "$expr" => {
        "$eq" => [
          { "$getField" => "weave_ref" },
          { "$literal" => call_ref },
        ],
      },
    },
  })
  rows = res.fetch("result", [])
  break if (expected_types - rows.map { |r| r["feedback_type"] }).empty?

  sleep 1
end

found_types = rows.map { |r| r["feedback_type"] }.sort
abort "FAIL: feedback for #{call_ref} not all visible after 5 reads (got #{found_types.inspect})" unless (expected_types - found_types).empty?

by_type = rows.each_with_object({}) { |r, h| h[r["feedback_type"]] = r }
abort "human payload: #{by_type[HUMAN_NOTE["feedback_type"]]["payload"].inspect}" unless by_type[HUMAN_NOTE["feedback_type"]]["payload"] == HUMAN_NOTE["payload"]
abort "scorer payload: #{by_type[SCORER_FEEDBACK["feedback_type"]]["payload"].inspect}" unless by_type[SCORER_FEEDBACK["feedback_type"]]["payload"] == SCORER_FEEDBACK["payload"]
rows.each do |row|
  abort "weave_ref drift: #{row["weave_ref"].inspect}" unless row["weave_ref"] == call_ref
end
puts "Verified: #{by_type.size} feedback items on #{call_ref}"
