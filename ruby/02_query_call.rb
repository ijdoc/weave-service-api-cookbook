#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 02: query Calls via /calls/stream_query.
#
# Demonstrates the workhorse read endpoint:
#   POST /calls/stream_query  -> stream NDJSON of matching Calls
#
# Sets up by creating one Call (op_name="recipe-02-query-call"), then
# queries that op_name and confirms the just-created Call appears in
# the streamed results.
#
# The endpoint returns one JSON object per line (application/jsonl).
# We parse line-by-line via a chunk buffer rather than reading the
# whole body, demonstrating the streaming pattern in Net::HTTP.
#
# Run:
#   ruby ruby/02_query_call.rb

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

OP_NAME = "recipe-02-query-call"
ATTRIBUTES = {
  "cookbook.language" => "ruby",
  "cookbook.recipe" => "02_query_call",
  "cookbook.environment" => ENV.fetch("COOKBOOK_ENVIRONMENT", "dev"),
}.freeze
INPUTS = { "question" => "What is the capital of Spain?" }.freeze
OUTPUT = { "answer" => "Madrid" }.freeze

def post_json(path, body)
  uri = URI.join(BASE_URL, path)
  req = Net::HTTP::Post.new(uri, "Content-Type" => "application/json")
  req.basic_auth("api", API_KEY)
  req.body = JSON.dump(body)
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for #{path}: #{res.body}" unless res.code.start_with?("2")
  res.body.empty? ? {} : JSON.parse(res.body)
end

# Streams /calls/stream_query response chunk-by-chunk, parsing each
# newline-delimited JSON object as it arrives. Returns the parsed rows.
def stream_query(body)
  uri = URI.join(BASE_URL, "/calls/stream_query")
  req = Net::HTTP::Post.new(uri, "Content-Type" => "application/json")
  req.basic_auth("api", API_KEY)
  req.body = JSON.dump(body)

  buffer = +""
  rows = []
  Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") do |http|
    http.request(req) do |res|
      abort "HTTP #{res.code} for /calls/stream_query: #{res.read_body}" unless res.code.start_with?("2")
      res.read_body do |chunk|
        buffer << chunk
        while (idx = buffer.index("\n"))
          line = buffer.slice!(0..idx).chomp
          rows << JSON.parse(line) unless line.empty?
        end
      end
    end
  end
  rows << JSON.parse(buffer) unless buffer.strip.empty?
  rows
end

# Setup: create + end a Call we can later query for.
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
puts "Created: id=#{call_id}"

post_json("/call/end", {
  "end" => {
    "project_id" => PROJECT_ID,
    "id" => call_id,
    "ended_at" => Time.now.utc.iso8601,
    "summary" => {},
    "output" => OUTPUT,
  },
})

# Query: stream Calls matching our op_name, newest first. Retry briefly
# to tolerate eventual consistency on the read path.
found = nil
5.times do
  results = stream_query({
    "project_id" => PROJECT_ID,
    "filter" => { "op_names" => [OP_NAME] },
    "sort_by" => [{ "field" => "started_at", "direction" => "desc" }],
    "limit" => 50,
  })
  # Require ended_at populated so we don't race the write-to-read
  # propagation and read a half-finalized row.
  found = results.find { |c| c["id"] == call_id && c["ended_at"] }
  break if found

  sleep 1
end

# --- verification ---
abort "FAIL: Call #{call_id} not in stream_query results after 5 attempts" if found.nil?

abort "op_name mismatch: #{found["op_name"].inspect}" unless found["op_name"] == OP_NAME
ATTRIBUTES.each do |k, v|
  abort "attribute #{k} mismatch: #{found["attributes"][k].inspect}" unless found["attributes"][k] == v
end
abort "inputs mismatch: #{found["inputs"].inspect}" unless found["inputs"] == INPUTS
abort "output mismatch: #{found["output"].inspect}" unless found["output"] == OUTPUT
abort "trace_id mismatch: #{found["trace_id"].inspect}" unless found["trace_id"] == trace_id
puts "Verified: id=#{call_id}"
