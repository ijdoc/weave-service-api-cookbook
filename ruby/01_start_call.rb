#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 01: start and finish a single Call.
#
# Demonstrates the minimum Call lifecycle:
#   POST /call/start  -> open the Call, capture id + trace_id
#   POST /call/end    -> close it
#
# Then verifies via POST /call/read.
#
# Run:
#   ruby ruby/01_start_call.rb

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

OP_NAME = "recipe-01-start-call"
ATTRIBUTES = {
  "cookbook.language" => "ruby",
  "cookbook.recipe" => "01_start_call",
}.freeze
INPUTS = { "question" => "What is the capital of France?" }.freeze
OUTPUT = { "answer" => "Paris" }.freeze

# Tiny POST helper. Centralizes auth + JSON serialization; the per-call
# payload shape remains visible at the call sites below.
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
trace_id = started.fetch("trace_id")
puts "Started: id=#{call_id} trace_id=#{trace_id}"

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

# --- verification ---
# Read the Call back and assert wire-state matches what we sent.
# Brief retry loop tolerates eventual consistency in the read path.
call = nil
5.times do
  res = post_json("/call/read", { "project_id" => PROJECT_ID, "id" => call_id })
  call = res["call"]
  break if call && call["ended_at"]

  sleep 1
end
abort "FAIL: Call #{call_id} not visible/finished after 5 reads" unless call && call["ended_at"]

abort "op_name mismatch: #{call["op_name"].inspect}" unless call["op_name"] == OP_NAME
ATTRIBUTES.each do |k, v|
  abort "attribute #{k} mismatch: #{call["attributes"][k].inspect}" unless call["attributes"][k] == v
end
abort "inputs mismatch: #{call["inputs"].inspect}" unless call["inputs"] == INPUTS
abort "output mismatch: #{call["output"].inspect}" unless call["output"] == OUTPUT
abort "trace_id mismatch: #{call["trace_id"].inspect}" unless call["trace_id"] == trace_id
puts "Verified: id=#{call_id}"
