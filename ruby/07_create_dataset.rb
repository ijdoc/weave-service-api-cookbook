#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 07: create a Dataset and read its rows back.
#
# Demonstrates the v2 Dataset endpoints plus the Table read needed to
# walk the rows:
#   POST   /v2/{entity}/{project}/datasets
#       -> create the Dataset, returns (object_id, digest, version_index)
#   GET    /v2/{entity}/{project}/datasets/{object_id}/versions/{digest}
#       -> read Dataset metadata, including a *reference* to its rows
#   POST   /table/query
#       -> read the actual rows out of the referenced Table
#
# Three wire-level points worth knowing:
#
# - These are the v2 endpoints under `/v2/{entity}/{project}/datasets`,
#   not a v1-style `POST /datasets/create`. Entity and project live in
#   the URL path rather than in the request body. Read uses GET (the
#   rest of the service API is POST-only); create uses POST with a JSON
#   body.
# - A Dataset is addressed by `(object_id, digest)`. `object_id` is
#   stable across versions; `digest` pins a specific version. Datasets
#   with the same `name` accumulate as new versions of one logical
#   Dataset. Datasets are *content-addressed* — identical (name, rows)
#   collapses to the same (digest, version_index). To make sure the
#   recipe actually exercises the write path on every run (rather than
#   silently resolving to an existing object), the dataset name is
#   stamped with a per-run Unix-epoch timestamp.
# - The Dataset read response's `rows` field is a *reference string* to
#   the underlying Table, not the row data. To walk rows, parse the
#   table digest out of that reference and call `/table/query`. Rows are
#   wrapped as {digest, val, original_index?} — the actual row content
#   lives under `val`.
#
# Run:
#   ruby ruby/07_create_dataset.rb

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

def request_json(req, path)
  uri = URI.join(BASE_URL, path)
  req.basic_auth("api", API_KEY)
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for #{path}: #{res.body}" unless res.code.start_with?("2")
  res.body.empty? ? {} : JSON.parse(res.body)
end

def post_json(path, body)
  uri = URI.join(BASE_URL, path)
  req = Net::HTTP::Post.new(uri, "Content-Type" => "application/json")
  req.body = JSON.dump(body)
  request_json(req, path)
end

def get_json(path)
  uri = URI.join(BASE_URL, path)
  request_json(Net::HTTP::Get.new(uri), path)
end

dataset_name = "recipe-07-dataset-#{Time.now.to_i}"
dataset_description = "Capital cities for evaluation (run at #{Time.now.utc.iso8601})"
dataset_rows = [
  { "question" => "What is the capital of France?", "answer" => "Paris" },
  { "question" => "What is the capital of Spain?", "answer" => "Madrid" },
  { "question" => "What is the capital of Italy?", "answer" => "Rome" },
]

# Create the Dataset. v2 path; entity + project go into the URL.
created = post_json("/v2/#{ENTITY}/#{PROJECT}/datasets", {
  "name" => dataset_name,
  "description" => dataset_description,
  "rows" => dataset_rows,
})
object_id = created.fetch("object_id")
digest = created.fetch("digest")
version_index = created.fetch("version_index")
puts "Created: object_id=#{object_id} digest=#{digest[0, 12]}… version=#{version_index}"

# Read Dataset metadata back. GET, with object_id + digest in the URL.
dataset = get_json("/v2/#{ENTITY}/#{PROJECT}/datasets/#{object_id}/versions/#{digest}")
abort "name: #{dataset["name"].inspect}" unless dataset["name"] == dataset_name
abort "description: #{dataset["description"].inspect}" unless dataset["description"] == dataset_description
abort "object_id drift: #{dataset["object_id"].inspect}" unless dataset["object_id"] == object_id
abort "digest drift: #{dataset["digest"].inspect}" unless dataset["digest"] == digest
puts "Read:    name=#{dataset["name"].inspect} rows_ref=#{dataset["rows"].inspect}"

# The rows field is a reference to a Table. Parse out the table digest
# so we can /table/query it. The format observed in practice is a
# weave URI like `weave:///{entity}/{project}/table/{digest}`; tolerate
# the bare-digest form too in case the shape varies.
rows_ref = dataset.fetch("rows")
m = rows_ref.match(%r{/table/([A-Za-z0-9_-]+)\z})
table_digest = m ? m[1] : rows_ref
puts "Table digest: #{table_digest[0, 12]}…"

# Query the actual rows.
table = post_json("/table/query", { "project_id" => PROJECT_ID, "digest" => table_digest })
rows = table.fetch("rows")

# --- verification ---
# Row count + first-row content must match what we wrote.
abort "row count: #{rows.size} vs #{dataset_rows.size}" unless rows.size == dataset_rows.size
# Row wrappers carry the row digest + the actual value under `val`.
rows.each_with_index do |row, i|
  abort "row #{i} val: #{row["val"].inspect} vs #{dataset_rows[i].inspect}" unless row["val"] == dataset_rows[i]
end

puts "Verified: #{rows.size} rows match (first: #{rows[0]["val"].inspect})"
