#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 04: update a Call's display_name after it finishes.
#
# Demonstrates the only mutation the service API exposes on a finished
# Call:
#   POST /call/update  -> change display_name
#
# Two wire-level quirks worth noting:
#
# - The body is *flat*: top-level `project_id`, `call_id`, `display_name`.
#   /call/start and /call/end wrap their bodies under `start` / `end`;
#   /call/update does not. Sending {"update": {...}} will 422.
# - The id field is named `call_id`, not `id` (which is what /call/end
#   uses).
#
# The schema's other constraint is that `display_name` is the only
# user-modifiable field. `attributes`, `inputs`, `output`, etc. are
# immutable after /call/start.
#
# Run:
#   ruby ruby/04_update_call.rb

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

OP_NAME = "recipe-04-update-call"
ATTRIBUTES = {
  "cookbook.language" => "ruby",
  "cookbook.recipe" => "04_update_call",
  "cookbook.environment" => ENV.fetch("COOKBOOK_ENVIRONMENT", "dev"),
}.freeze
INPUTS = { "question" => "What is the capital of Italy?" }.freeze
OUTPUT = { "answer" => "Rome" }.freeze
NEW_DISPLAY_NAME = "recipe 04 — updated after finish"

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

# Mutate display_name. Flat body, `call_id` (not `id`), no wrapper key.
post_json("/call/update", {
  "project_id" => PROJECT_ID,
  "call_id" => call_id,
  "display_name" => NEW_DISPLAY_NAME,
})
puts "Updated: id=#{call_id} display_name=#{NEW_DISPLAY_NAME.inspect}"

# --- verification ---
# Read the Call back and assert display_name reflects the update.
# Brief retry loop tolerates eventual consistency in the read path.
call = nil
5.times do
  res = post_json("/call/read", { "project_id" => PROJECT_ID, "id" => call_id })
  call = res["call"]
  break if call && call["display_name"] == NEW_DISPLAY_NAME

  sleep 1
end

abort "FAIL: Call #{call_id} display_name not updated after 5 reads" if call.nil? || call["display_name"] != NEW_DISPLAY_NAME

abort "display_name: #{call["display_name"].inspect}" unless call["display_name"] == NEW_DISPLAY_NAME
# op_name and the rest must NOT have changed — /call/update only touches display_name.
abort "op_name drifted: #{call["op_name"].inspect}" unless call["op_name"] == OP_NAME
ATTRIBUTES.each do |k, v|
  abort "attribute #{k}: #{call["attributes"][k].inspect}" unless call["attributes"][k] == v
end
abort "inputs: #{call["inputs"].inspect}" unless call["inputs"] == INPUTS
abort "output: #{call["output"].inspect}" unless call["output"] == OUTPUT
abort "trace_id: #{call["trace_id"].inspect}" unless call["trace_id"] == trace_id
puts "Verified: id=#{call_id} display_name=#{call["display_name"].inspect}"
