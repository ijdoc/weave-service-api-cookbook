#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 03: parent + child Calls (RAG-shaped trace).
#
# Demonstrates Trace structure: one parent Call with two child Calls
# underneath. Children declare their parent via `parent_id` on
# /call/start and share the parent's `trace_id` explicitly.
#
# The RAG-shaped flow:
#     rag_pipeline (parent)
#     ├── retrieve  (child 1)
#     └── generate  (child 2)
#
# Ordering matters: a child's /call/start happens after the parent's
# /call/start, and each child's /call/end happens before the parent's
# /call/end. The recipe shows this canonical order.
#
# Run:
#   ruby ruby/03_parent_child_calls.rb

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
  "cookbook.recipe" => "03_parent_child_calls",
  "cookbook.environment" => ENV.fetch("COOKBOOK_ENVIRONMENT", "dev"),
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

def start_call(op_name, inputs, parent_id: nil, trace_id: nil)
  payload = {
    "start" => {
      "project_id" => PROJECT_ID,
      "op_name" => op_name,
      "started_at" => Time.now.utc.iso8601,
      "attributes" => BASE_ATTRIBUTES,
      "inputs" => inputs,
    },
  }
  payload["start"]["parent_id"] = parent_id if parent_id
  payload["start"]["trace_id"] = trace_id if trace_id
  post_json("/call/start", payload)
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

# Open the parent (top-level: no parent_id, no explicit trace_id).
# The server assigns a trace_id which we propagate to children.
parent = start_call("recipe-03-rag-pipeline", { "question" => "Where is the Eiffel Tower?" })
parent_id = parent.fetch("id")
trace_id = parent.fetch("trace_id")
puts "Started parent: id=#{parent_id} trace_id=#{trace_id}"

# Open + finish the first child (retrieve), passing the parent's id and trace_id.
retrieve = start_call(
  "recipe-03-retrieve",
  { "question" => "Where is the Eiffel Tower?" },
  parent_id: parent_id,
  trace_id: trace_id,
)
retrieve_id = retrieve.fetch("id")
puts "Started child 1: id=#{retrieve_id}"
end_call(retrieve_id, { "docs" => ["Paris", "France"] })
puts "Ended   child 1: id=#{retrieve_id}"

# Open + finish the second child (generate).
generate = start_call(
  "recipe-03-generate",
  { "docs" => ["Paris", "France"], "question" => "Where is the Eiffel Tower?" },
  parent_id: parent_id,
  trace_id: trace_id,
)
generate_id = generate.fetch("id")
puts "Started child 2: id=#{generate_id}"
end_call(generate_id, { "answer" => "In Paris, France." })
puts "Ended   child 2: id=#{generate_id}"

# Close the parent (after all children have finished).
end_call(parent_id, { "answer" => "In Paris, France." })
puts "Ended   parent:  id=#{parent_id}"

# --- verification ---
expected = [parent_id, retrieve_id, generate_id]
found_by_id = {}
5.times do
  rows = stream_query({
    "project_id" => PROJECT_ID,
    "filter" => { "trace_ids" => [trace_id] },
  })
  found_by_id = rows.each_with_object({}) { |c, h| h[c["id"]] = c }
  # Require all three visible AND finalized (ended_at populated) so we
  # don't race write-to-read propagation on inner-field reads.
  break if (expected - found_by_id.keys).empty? && expected.all? { |i| found_by_id[i]["ended_at"] }

  sleep 1
end

missing_ids = expected - found_by_id.keys
abort "FAIL: trace #{trace_id} missing calls: #{missing_ids.inspect}" unless missing_ids.empty?

parent_call = found_by_id[parent_id]
retrieve_call = found_by_id[retrieve_id]
generate_call = found_by_id[generate_id]

abort "parent has parent_id: #{parent_call["parent_id"].inspect}" unless parent_call["parent_id"].nil?
abort "retrieve.parent_id: #{retrieve_call["parent_id"].inspect}" unless retrieve_call["parent_id"] == parent_id
abort "generate.parent_id: #{generate_call["parent_id"].inspect}" unless generate_call["parent_id"] == parent_id

[parent_call, retrieve_call, generate_call].each do |c|
  abort "trace_id on #{c["id"]}: #{c["trace_id"].inspect}" unless c["trace_id"] == trace_id
  BASE_ATTRIBUTES.each do |k, v|
    abort "attribute #{k} on #{c["id"]}: #{c["attributes"][k].inspect}" unless c["attributes"][k] == v
  end
end

puts "Verified: trace_id=#{trace_id} (1 parent + 2 children)"
