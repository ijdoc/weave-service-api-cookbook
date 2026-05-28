#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 08: create a versioned Model + use it in a trace.
#
# First application of *ADR-0004* (the source-embedding scaffold). The
# recipe creates two Weave Objects:
#
#     POST /v2/{entity}/{project}/ops   -> register the predict Op
#                                          (Python scaffold per ADR-0004)
#     POST /obj/create                  -> register the Model object,
#                                          pointing val.predict at the
#                                          predict Op's weave:// ref
#
# Then it opens a Call that references both — establishing the
# "predict logic lives in the recipe file; Weave records identity +
# invocation" pattern that recipes 09–12 reuse.
#
# Three wire-level points worth knowing:
#
# - *The Model is created via `/obj/create`, NOT `/v2/.../models`.*
#   The specialized endpoint stashes the entire source into
#   `files.obj.py` as a single "code tab" attachment and does NOT add
#   per-method ref fields. The W&B UI's Model page renders methods
#   inline only when the val carries a `<method>: <weave:// op ref>`
#   field. The SDK uses the generic Object endpoint with structured
#   metadata for exactly this reason; the cookbook follows suit.
# - The Model val mirrors the SDK shape: `_bases=["Model", "Object",
#   "BaseModel"]`, `_class_name=<subclass>`, `_type=<subclass>`, a
#   `predict` field pointing at the predict Op's weave:// ref, plus
#   *instance attributes that represent the model's instantiation
#   config*. Realistic attributes here are `model_name`, `temperature`,
#   `max_tokens` — the values that distinguish one Model version from
#   another. *Per-Call data* like the question being asked and the
#   answer returned live in the Call's inputs / output, NOT on the
#   Model. Editing a Model attribute is a versioning event; logging a
#   new Call is not.
# - The UI's CallPage parses `op_name` and `inputs.self` as weave://
#   URIs and crashes on raw strings — both MUST be real refs.
#
# Editing this file changes its SHA256 -> the Op scaffold changes ->
# Weave bumps the predict Op's version_index. Per-language identity
# comes from the Model + Op object_ids (`recipe-08-model-<lang>` and
# `recipe-08-model-<lang>.predict`).
#
# For brevity this recipe mocks the actual LLM invocation — the Call's
# output is a hardcoded answer. A real recipe would call the LLM named
# in `model_name` with the Model's `temperature` / `max_tokens`
# settings and the rendered prompt (recipe 07 covers prompts), then
# surface the response.
#
# Run:
#   ruby ruby/08_use_model.rb

require "digest"
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

# --- ADR-0004 scaffold for the predict Op ---
# SHA256 of this recipe file's bytes. Edits flow through to OP_SOURCE
# below, which is what Weave content-addresses on. Re-running an
# unchanged file is idempotent; editing bumps the predict Op version.
RECIPE_PATH = "ruby/08_use_model.rb"
RECIPE_SHA = Digest::SHA256.hexdigest(File.read(__FILE__))[0, 16]

OP_SOURCE = <<~PY
  # Cookbook scaffold (ruby)
  # Source: #{RECIPE_PATH}
  # SHA256: #{RECIPE_SHA}

  import weave


  @weave.op
  def predict(self, question):
      """The actual predict implementation lives in:
          #{RECIPE_PATH}

      Byte-for-byte reference (SHA256 of the recipe file):
          #{RECIPE_SHA}

      To verify a local copy of the file matches (POSIX shell):
          shasum -a 256 #{RECIPE_PATH} | cut -c1-16

      This Python op is a metadata handle, not the real model — running
      it raises NotImplementedError by design.
      """
      raise NotImplementedError(
          "This op is a Python scaffold uploaded from a non-Python recipe. "
          "See the docstring above for the real source-language file and a "
          "verifiable byte-for-byte reference (SHA256)."
      )
PY

def post_json(path, body)
  uri = URI.join(BASE_URL, path)
  req = Net::HTTP::Post.new(uri, "Content-Type" => "application/json")
  req.basic_auth("api", API_KEY)
  req.body = JSON.dump(body)
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for #{path}: #{res.body}" unless res.code.start_with?("2")
  res.body.empty? ? {} : JSON.parse(res.body)
end

def now
  Time.now.utc.iso8601
end

# 1) Register the predict Op via the specialized /v2/.../ops endpoint.
op_name = "recipe-08-model-ruby.predict"
op_res = post_json("/v2/#{ENTITY}/#{PROJECT}/ops", { "name" => op_name, "source_code" => OP_SOURCE })
predict_op_ref = "weave:///#{PROJECT_ID}/op/#{op_res["object_id"]}:#{op_res["digest"]}"
puts "Predict op: #{op_res["object_id"]} digest=#{op_res["digest"][0, 12]}… version=#{op_res["version_index"]}"

# 2) Register the Model via the generic /obj/create endpoint.
# Instance attributes here are the kind of config a real Model would
# carry — change any value and you get a new (digest, version_index).
# Q&A specifics (the question, the answer) belong on the Call, not the
# Model.
model_object_id = "recipe-08-model-ruby"
model_val = {
  "_bases" => ["Model", "Object", "BaseModel"],
  "_class_name" => "Recipe08RubyModel",
  "_type" => "Recipe08RubyModel",
  "name" => model_object_id,
  "description" => "Cookbook model handle (ruby recipe 08)",
  "model_name" => "gpt-4o-mini",
  "temperature" => 0.7,
  "max_tokens" => 100,
  "predict" => predict_op_ref,
}
model_res = post_json("/obj/create", {
  "obj" => {
    "project_id" => PROJECT_ID,
    "object_id" => model_object_id,
    "val" => model_val,
  },
})
model_digest = model_res.fetch("digest")
model_ref = "weave:///#{PROJECT_ID}/object/#{model_object_id}:#{model_digest}"
puts "Model:      #{model_res["object_id"]} digest=#{model_digest[0, 12]}…"
puts "  ref: #{model_ref}"

# 3) Open a Call that uses the predict Op + Model.
question = "Is the sky blue?"
res = post_json("/call/start", {
  "start" => {
    "project_id" => PROJECT_ID,
    "op_name" => predict_op_ref,
    "started_at" => now,
    "attributes" => {
      "cookbook.language" => "ruby",
      "cookbook.recipe" => "08_use_model",
      "cookbook.environment" => ENV.fetch("COOKBOOK_ENVIRONMENT", "dev"),
    },
    "inputs" => { "self" => model_ref, "question" => question },
  },
})
call_id = res.fetch("id")
trace_id = res.fetch("trace_id")
puts "Started:    id=#{call_id}"

# 4) End the Call with the model's answer.
# A real recipe would call `model_val["model_name"]` here with the
# question and the model's temperature/max_tokens settings, and use
# the LLM's response as the Call's output. We hardcode an answer so
# the cookbook stays focused on the wire-level Model + Op + Call
# wiring.
answer = "yes"
post_json("/call/end", {
  "end" => {
    "project_id" => PROJECT_ID,
    "id" => call_id,
    "ended_at" => now,
    "summary" => {
      "status_counts" => { "success" => 1, "error" => 0 },
      "weave" => { "status" => "success", "trace_name" => op_name },
    },
    "output" => answer,
  },
})
puts "Ended:      id=#{call_id} output=#{answer.inspect}"

# --- verification ---
call = nil
5.times do
  body = post_json("/call/read", { "project_id" => PROJECT_ID, "id" => call_id })
  call = body["call"]
  break if call && call["ended_at"]

  sleep 1
end
abort "FAIL: Call #{call_id} not visible/finished after 5 reads" if call.nil? || call["ended_at"].nil?

abort "op_name: #{call["op_name"].inspect}" unless call["op_name"] == predict_op_ref
abort "inputs.self: #{call["inputs"]["self"].inspect}" unless call["inputs"]["self"] == model_ref
abort "inputs.question: #{call["inputs"]["question"].inspect}" unless call["inputs"]["question"] == question
abort "output: #{call["output"].inspect}" unless call["output"] == answer
abort "trace_id: #{call["trace_id"].inspect}" unless call["trace_id"] == trace_id
puts "Verified:   id=#{call_id} (op + model + output round-tripped)"
