#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 09: create a Scorer Op + score a Call (the apply_scorer pattern).
#
# Wire-level equivalent of the SDK's `result.call.apply_scorer(scorer)`
# pattern — score an arbitrary already-logged Call without dragging in
# the full evaluation flow (recipes 11-13). Reuses the ADR-0004
# Op-creation pattern from recipe 08, this time for a scorer function.
#
# A *Scorer Op* is just an Op whose role is to score a Call's output.
# There is no separate Scorer Object class to register here — the W&B
# service does expose `POST /v2/.../scorers` (a dedicated Scorer object
# endpoint), but the cookbook does not use it; the Op pattern is what
# `@weave.op` scorer functions use and what `apply_scorer` integrates
# with under the hood.
#
# This recipe builds three things on the wire:
#
# 1. A small model Call producing a sample prediction (mirrors recipe
#    08's predict shape but simpler — we skip the Model object and the
#    predict Op, just open a Call directly).
# 2. A scoring Call invoking the Scorer Op, with the prediction +
#    expected answer as inputs and the score value as output. This is
#    a top-level standalone Call (no parent_id; separate trace) — same
#    shape `apply_scorer` produces.
# 3. A *`wandb.runnable.<scorer_op_id>`* Feedback row attached to the
#    prediction Call. *This Feedback is the load-bearing link that
#    makes the score render inline under the prediction in the W&B UI.*
#    Without it, the score Call would be a disconnected island.
#
# Wire-level points worth knowing:
#
# - The *`wandb.runnable.*`* Feedback convention is how SDK
#   `apply_scorer` ties a standalone scoring Call back to a prediction
#   Call. The Feedback row carries:
#       feedback_type = "wandb.runnable.<scorer_op_id>"
#       payload       = {"output": <score value>}
#       runnable_ref  = <Scorer Op weave:// ref>
#       call_ref      = <score Call weave:// ref>
#   The UI reads `wandb.runnable.*` Feedbacks on the prediction Call
#   and shows the score (plus a link to the score Call). This is the
#   same Feedback endpoint family covered in recipes 05-06, just with
#   a specific feedback_type pattern Weave recognises.
# - Scorer-Op scoring (this recipe) and plain `feedback_type` scoring
#   (recipe 06 — `wandb.note.1`, `wandb.reaction.1`, arbitrary user
#   types) coexist. The structured eval flow (recipe 12) uses scorer
#   Ops + nested children under `Evaluation.predict_and_score`, plus
#   matching Feedback rows. Recipe 09 is the standalone apply-scorer-
#   to-an-existing-call shape.
# - Scorer Op object_ids are NOT aggregator-filtered, so per-language
#   naming (`recipe-09-is-correct-{python,ruby,dotnet}`) is fine. The
#   canonical Eval Op names in recipe 12 (`Evaluation.evaluate` etc.)
#   *are* aggregator-filtered, which is why those stay shared.
# - The Scorer Op's source carries the ADR-0004 scaffold (header +
#   in-method docstring + raise NotImplementedError + shasum verify
#   hint).
#
# Run:
#   ruby ruby/09_score_a_call.rb

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

# --- ADR-0004 scaffold for the Scorer Op ---
RECIPE_PATH = "ruby/09_score_a_call.rb"
RECIPE_SHA = Digest::SHA256.hexdigest(File.read(__FILE__))[0, 16]

SCORER_SOURCE = <<~PY
  # Cookbook scaffold (ruby)
  # Source: #{RECIPE_PATH}
  # SHA256: #{RECIPE_SHA}

  import weave


  @weave.op
  def is_correct(output, expected):
      """The actual scoring implementation lives in:
          #{RECIPE_PATH}

      Byte-for-byte reference (SHA256 of the recipe file):
          #{RECIPE_SHA}

      To verify a local copy of the file matches (POSIX shell):
          shasum -a 256 #{RECIPE_PATH} | cut -c1-16

      This Python op is a metadata handle, not the real scorer — running
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

# 1) Register the Scorer Op. Per-language object_id; the server
# lowercases it. Per the docstring, Scorer Op names are not
# aggregator-filtered, so per-language identity is fine.
scorer_op_id = "recipe-09-is-correct-ruby"
scorer_res = post_json("/v2/#{ENTITY}/#{PROJECT}/ops", { "name" => scorer_op_id, "source_code" => SCORER_SOURCE })
scorer_op_ref = "weave:///#{PROJECT_ID}/op/#{scorer_res["object_id"]}:#{scorer_res["digest"]}"
puts "Scorer op:  #{scorer_res["object_id"]} digest=#{scorer_res["digest"][0, 12]}… version=#{scorer_res["version_index"]}"

# 2) Produce a sample prediction via a tiny model Call, then score it
# with the Scorer Op as a SEPARATE top-level Call. The link between
# them isn't structural (no parent_id) — it's a `wandb.runnable.*`
# Feedback row created in step 4, mirroring what the SDK's
# `apply_scorer` does under the hood.
question = "Is the sky blue?"
expected = "yes"

res = post_json("/call/start", {
  "start" => {
    "project_id" => PROJECT_ID,
    "op_name" => "recipe-09-mock-predict",
    "started_at" => now,
    "attributes" => {
      "cookbook.language" => "ruby",
      "cookbook.recipe" => "09_score_a_call",
      "cookbook.environment" => ENV.fetch("COOKBOOK_ENVIRONMENT", "dev"),
    },
    "inputs" => { "question" => question },
  },
})
predict_call_id = res.fetch("id")
trace_id = res.fetch("trace_id")
prediction = "yes"
post_json("/call/end", {
  "end" => {
    "project_id" => PROJECT_ID,
    "id" => predict_call_id,
    "ended_at" => now,
    "summary" => {
      "status_counts" => { "success" => 1, "error" => 0 },
      "weave" => { "status" => "success", "trace_name" => "recipe-09-mock-predict" },
    },
    # Per the cookbook's question/answer convention (CONTRIBUTING.md),
    # predict outputs land under an `answer` key. The Scorer Op below
    # still takes the raw answer value as its `output` argument —
    # that's the scorer's signature, not the predict's output shape.
    "output" => { "answer" => prediction },
  },
})
puts "Predicted:  id=#{predict_call_id} output=#{prediction.inspect}"

# 3) Open a top-level scoring Call invoking the Scorer Op. op_name MUST
# be the Op's weave:// ref (not a bare string) for the UI to render
# the Op inline. Inputs are what's being scored (prediction +
# expected); output is the score value (boolean here — Eval Result
# aggregation in recipe 13 classifies this as a binary value type).
#
# Inputs use raw values here for simplicity. In the full eval flow
# (recipe 12), the SDK refs Dataset row fields and Model attributes
# via weave:// URIs so the UI can navigate back to the source — see
# recipe 12 for that richer shape.
res = post_json("/call/start", {
  "start" => {
    "project_id" => PROJECT_ID,
    "op_name" => scorer_op_ref,
    "started_at" => now,
    "attributes" => {
      "cookbook.language" => "ruby",
      "cookbook.recipe" => "09_score_a_call",
      "cookbook.environment" => ENV.fetch("COOKBOOK_ENVIRONMENT", "dev"),
    },
    "inputs" => { "output" => prediction, "expected" => expected },
  },
})
score_call_id = res.fetch("id")
score = prediction == expected
post_json("/call/end", {
  "end" => {
    "project_id" => PROJECT_ID,
    "id" => score_call_id,
    "ended_at" => now,
    "summary" => {
      "status_counts" => { "success" => 1, "error" => 0 },
      "weave" => { "status" => "success", "trace_name" => scorer_op_id },
    },
    "output" => score,
  },
})
puts "Scored:     id=#{score_call_id} output=#{score.inspect}"

# 4) Link the score to the prediction Call by creating a
# `wandb.runnable.<scorer_op_id>` Feedback row on the prediction.
# This is the load-bearing step — the W&B UI uses this Feedback (not
# any parent-child structure) to render the score inline on the
# prediction Call's view. The SDK's `apply_scorer` posts this exact
# shape under the hood.
predict_call_ref = "weave:///#{PROJECT_ID}/call/#{predict_call_id}"
score_call_ref = "weave:///#{PROJECT_ID}/call/#{score_call_id}"
feedback_res = post_json("/feedback/create", {
  "project_id" => PROJECT_ID,
  "weave_ref" => predict_call_ref,
  "feedback_type" => "wandb.runnable.#{scorer_op_id}",
  "payload" => { "output" => score },
  "runnable_ref" => scorer_op_ref,
  "call_ref" => score_call_ref,
})
puts "Linked:     feedback id=#{feedback_res["id"]} on predict call (feedback_type=wandb.runnable.#{scorer_op_id})"

# --- verification ---
# (a) The scoring Call round-trips with the right op_ref + inputs +
#     boolean output.
# (b) The wandb.runnable.* Feedback exists on the prediction Call
#     and carries the score value + scorer Op ref + score Call ref.
call = nil
5.times do
  body = post_json("/call/read", { "project_id" => PROJECT_ID, "id" => score_call_id })
  call = body["call"]
  break if call && call["ended_at"]

  sleep 1
end
abort "FAIL: scoring Call #{score_call_id} not visible/finished after 5 reads" if call.nil? || call["ended_at"].nil?

abort "op_name: #{call["op_name"].inspect}" unless call["op_name"] == scorer_op_ref
abort "inputs.output: #{call["inputs"]["output"].inspect}" unless call["inputs"]["output"] == prediction
abort "inputs.expected: #{call["inputs"]["expected"].inspect}" unless call["inputs"]["expected"] == expected
abort "output: #{call["output"].inspect}" unless call["output"] == score

# Verify the wandb.runnable.* Feedback row exists on the prediction
# Call. /feedback/query filtered by weave_ref + feedback_type lands
# the same row we posted.
expected_feedback_type = "wandb.runnable.#{scorer_op_id}"
feedback_rows = nil
5.times do
  body = post_json("/feedback/query", {
    "project_id" => PROJECT_ID,
    "query" => { "$expr" => { "$eq" => [
      { "$getField" => "weave_ref" },
      { "$literal" => predict_call_ref },
    ] } },
  })
  feedback_rows = body["result"] || []
  break if feedback_rows.any? { |row| row["feedback_type"] == expected_feedback_type }

  sleep 1
end
abort "FAIL: no #{expected_feedback_type.inspect} feedback on #{predict_call_ref} after 5 reads" unless feedback_rows.any? { |row| row["feedback_type"] == expected_feedback_type }

linking = feedback_rows.find { |row| row["feedback_type"] == expected_feedback_type }
abort "payload: #{linking["payload"].inspect}" unless linking["payload"] == { "output" => score }
abort "runnable_ref: #{linking["runnable_ref"].inspect}" unless linking["runnable_ref"] == scorer_op_ref
abort "call_ref: #{linking["call_ref"].inspect}" unless linking["call_ref"] == score_call_ref

puts "Verified:   id=#{score_call_id} (scorer op + inputs + score output round-tripped)"
puts "Verified:   wandb.runnable.#{scorer_op_id} feedback links score -> predict"
