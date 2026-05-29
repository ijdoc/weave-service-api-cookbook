#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 07: publish a Prompt + reference it from a Call + tag/alias it.
#
# Introduces four new things that recipes 08-13 build on:
#
#     POST /obj/create                            -> generic Weave Object
#                                                    endpoint; here, publish
#                                                    a StringPrompt
#     POST /obj/read                              -> read it back
#     PUT  /objs/{id}/versions/{digest}/tags      -> add version tags
#     PUT  /objs/{id}/aliases                     -> set named pointers
#
#     (and the existing /call/start + /call/end, but now with
#      `inputs.prompt` = a weave:// ref to the Prompt — the "object
#      ref in trace inputs" pattern that unlocks Model.predict,
#      Scorer Ops, and the eval flow)
#
# Five wire-level points worth knowing:
#
# - The Object endpoint is *flat under an `obj` wrapper*:
#       {"obj" => {"project_id", "object_id", "val"}}
#   The val you submit is what Weave stores verbatim (after lowercasing
#   the `object_id`). The val MUST carry `_bases`, `_class_name`, and
#   `_type` for the Weave UI to recognise the object — the server does
#   not auto-fill these. An optional `builtin_object_class` field on
#   the request must match val's `_class_name` exactly when set;
#   omitting it is cleaner (the val is the single source of truth on
#   class info).
# - `base_object_class="Prompt"` (what the W&B UI's Prompts page
#   filters on) is derived by the server from `val._bases`;
#   `leaf_object_class` comes from `val._class_name`. A one-line
#   variant for messages-shaped prompts is `MessagesPrompt` (list of
#   `{role, content}` dicts) — not demonstrated here, but the same val
#   shape applies (`_class_name` / `_type` become "MessagesPrompt",
#   and a `messages` field replaces `content`).
# - A Prompt is content-addressed: identical val collapses to the same
#   (digest, version_index). Editing the content (or any other val
#   field) bumps the version. No timestamping needed; this recipe's
#   per-language identity comes from a different `object_id` per port.
# - *Tags vs aliases* — both UI-visible Object metadata, separate from
#   val (so changing them does NOT bump the version):
#     * Tags are per-VERSION, additive labels (e.g., "dev", "production",
#       "reviewed"). PUT adds, POST .../remove removes. Many versions
#       can share a tag.
#     * Aliases are per-object_id named pointers — re-PUTting an alias
#       detaches it from the prior version. The server auto-maintains
#       a `latest` alias on the most-recent version; do not set it
#       yourself.
#   These same endpoints apply to any Weave Object (Model, Dataset,
#   Evaluation, Scorer Op), not just Prompts.
# - *Val "extras"* — you can also stuff arbitrary JSON fields directly
#   into val (any type, nested hashes, etc.) alongside the canonical
#   `content`/`description`/`name`. They round-trip cleanly and are
#   queryable via /objs/query filters, but DO NOT appear in dedicated
#   UI columns or panels — only `tags` and `aliases` do. Use val
#   extras for structured machine-queryable metadata; use tags/aliases
#   for UI-visible labels and pointers.
#
# Run:
#   ruby ruby/07_use_prompt.rb

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

def request_json(method_class, path, body)
  uri = URI.join(BASE_URL, path)
  req = method_class.new(uri, "Content-Type" => "application/json")
  req.basic_auth("api", API_KEY)
  req.body = JSON.dump(body)
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for #{path}: #{res.body}" unless res.code.start_with?("2")
  res.body.empty? ? {} : JSON.parse(res.body)
end

def post_json(path, body)
  request_json(Net::HTTP::Post, path, body)
end

def put_json(path, body)
  request_json(Net::HTTP::Put, path, body)
end

# 1) Publish a StringPrompt via the generic Object endpoint.
#
# val "extras": you could add arbitrary JSON fields here alongside the
# canonical ones below (e.g., "owner_email" => "alice@example.com",
# "model_target" => "gpt-4o-mini", "custom_attributes" => {...}).
# They'd round-trip cleanly and be queryable via /objs/query filters,
# but would NOT appear in dedicated UI columns. For UI-visible
# metadata, use the tags + aliases steps further down.
prompt_object_id = "recipe-07-prompt-ruby"
prompt_val = {
  "_bases" => ["Prompt", "Object", "BaseModel"],
  "_class_name" => "StringPrompt",
  "_type" => "StringPrompt",
  "name" => nil,
  "description" => "Capital-city Q&A prompt template (ruby recipe 07)",
  "content" => "Answer the question concisely: {question}",
}
created = post_json("/obj/create", {
  "obj" => {
    "project_id" => PROJECT_ID,
    "object_id" => prompt_object_id,
    "val" => prompt_val,
  },
})
prompt_digest = created.fetch("digest")
prompt_ref = "weave:///#{PROJECT_ID}/object/#{prompt_object_id}:#{prompt_digest}"
puts "Published: #{prompt_object_id} digest=#{prompt_digest[0, 12]}…"
puts "  ref: #{prompt_ref}"

# 2) Tag this version with the current cookbook environment ("dev" or
# "ci"). Tags are a first-class, per-version, UI-visible metadata
# channel — separate from val. PUT is additive (re-runs are no-ops if
# the tag is already present); removal uses POST /objs/.../tags/remove
# with the same body shape. The same endpoint applies to any Weave
# Object (Model, Dataset, Evaluation, Scorer Op).
env_tag = ENV.fetch("COOKBOOK_ENVIRONMENT", "dev")
tags_to_add = [env_tag, "ruby"]
put_json("/objs/#{prompt_object_id}/versions/#{prompt_digest}/tags", {
  "project_id" => PROJECT_ID,
  "tags" => tags_to_add,
})
puts "Tagged:    #{tags_to_add.inspect} -> version #{prompt_digest[0, 12]}…"

# 3) Add named aliases pointing at this version. Aliases are
# per-object_id named pointers — typical examples are deployment
# targets ("staging", "production") and release candidates
# ("v1-candidate"). PUT adds; use POST /objs/{id}/aliases/remove to
# detach an alias. The server also auto-maintains a `latest` alias on
# the most-recent version; do not try to set "latest" yourself.
aliases_to_set = ["staging", "v1-candidate"]
put_json("/objs/#{prompt_object_id}/aliases", {
  "project_id" => PROJECT_ID,
  "digest" => prompt_digest,
  "aliases" => aliases_to_set,
})
puts "Aliased:   #{aliases_to_set.inspect} -> version #{prompt_digest[0, 12]}…"

# 4) Read it back (with tags + aliases) and assert everything
# round-trips.
read_back = post_json("/obj/read", {
  "project_id" => PROJECT_ID,
  "object_id" => prompt_object_id,
  "digest" => prompt_digest,
  "include_tags_and_aliases" => true,
})
obj = read_back.fetch("obj")
abort "_class_name: #{obj["val"]["_class_name"].inspect}" unless obj["val"]["_class_name"] == "StringPrompt"
abort "content: #{obj["val"]["content"].inspect}" unless obj["val"]["content"] == prompt_val["content"]
abort "base_object_class: #{obj["base_object_class"].inspect}" unless obj["base_object_class"] == "Prompt"
abort "leaf_object_class: #{obj["leaf_object_class"].inspect}" unless obj["leaf_object_class"] == "StringPrompt"
tags = obj["tags"] || []
aliases = obj["aliases"] || []
tags_to_add.each do |t|
  abort "tag #{t.inspect} missing from #{tags.inspect}" unless tags.include?(t)
end
aliases_to_set.each do |a|
  abort "alias #{a.inspect} missing from #{aliases.inspect}" unless aliases.include?(a)
end
puts "Read:      version=#{obj["version_index"]} tags=#{tags.inspect} aliases=#{aliases.inspect}"

# 3) Open a Call whose `inputs.prompt` is the Prompt's weave:// ref.
question = "What is the capital of France?"
res = post_json("/call/start", {
  "start" => {
    "project_id" => PROJECT_ID,
    "op_name" => "recipe-07-prompt-in-trace",
    "started_at" => Time.now.utc.iso8601,
    "attributes" => {
      "cookbook.language" => "ruby",
      "cookbook.recipe" => "07_use_prompt",
      "cookbook.environment" => ENV.fetch("COOKBOOK_ENVIRONMENT", "dev"),
    },
    "inputs" => { "prompt" => prompt_ref, "question" => question },
  },
})
call_id = res.fetch("id")
trace_id = res.fetch("trace_id")
puts "Started:   id=#{call_id} (inputs.prompt = #{prompt_ref})"

# Client-side: substitute the question into the prompt template.
rendered = prompt_val["content"].sub("{question}", question)
answer = "Paris"

post_json("/call/end", {
  "end" => {
    "project_id" => PROJECT_ID,
    "id" => call_id,
    "ended_at" => Time.now.utc.iso8601,
    "summary" => {},
    "output" => { "rendered_prompt" => rendered, "answer" => answer },
  },
})
puts "Ended:     id=#{call_id} output.answer=#{answer.inspect}"

# --- verification ---
call = nil
5.times do
  body = post_json("/call/read", { "project_id" => PROJECT_ID, "id" => call_id })
  call = body["call"]
  break if call && call["ended_at"]

  sleep 1
end
abort "FAIL: Call #{call_id} not visible/finished after 5 reads" if call.nil? || call["ended_at"].nil?

abort "inputs.prompt: #{call["inputs"]["prompt"].inspect}" unless call["inputs"]["prompt"] == prompt_ref
abort "inputs.question: #{call["inputs"]["question"].inspect}" unless call["inputs"]["question"] == question
abort "output.answer: #{call["output"]["answer"].inspect}" unless call["output"]["answer"] == answer
abort "trace_id: #{call["trace_id"].inspect}" unless call["trace_id"] == trace_id
puts "Verified:  prompt ref round-trips in call inputs"
