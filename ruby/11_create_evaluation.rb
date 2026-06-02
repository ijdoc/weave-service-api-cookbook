#!/usr/bin/env ruby
# frozen_string_literal: true

# Recipe 11: set up an Evaluation Object.
#
# Pulls everything from earlier recipes together into a single Evaluation
# *definition* — the versioned, content-addressed Object that recipe 12
# will execute and recipe 13 will query against. After this recipe runs,
# the W&B UI's *Evaluation Definitions* page (`/weave/evaluation-definitions`)
# shows it as a browsable definition with no associated runs yet.
#
# The recipe builds two kinds of artifacts:
#
# 1. *Three canonical Eval Ops* (`Evaluation.evaluate`,
#    `Evaluation.predict_and_score`, `Evaluation.summarize`) — inert
#    lifecycle-marker Ops registered via a two-step
#    `/file/create` + `/obj/create` flow with ADR-0004 scaffolds.
#    The W&B service identifies these Ops by their `object_id` and
#    uses them to recognise an evaluation Call trace
#    (`/eval_results/query` filters on the exact canonical names,
#    case-sensitive). The source is a stub `raise NotImplementedError`;
#    the real eval logic lives in recipe 12 client-side.
#    Content-addressed — re-running an unchanged recipe 11 is a no-op;
#    editing this recipe bumps the Op versions (and downstream the
#    Eval Object version too).
#
# 2. *The Evaluation Object itself* — built via `POST /obj/create`
#    with `builtin_object_class="Evaluation"`, referencing the freshly
#    registered canonical Ops + the recipe-08 Model + the recipe-09
#    Scorer Op + the recipe-10 Dataset, all by weave:// URI.
#
# Recipe 12 (Run an evaluation) will look up the canonical Eval Ops and
# the Eval Object created here; recipe 13 (Query results) does the same.
# *Don't duplicate the scaffolds in recipes 12 / 13* — they live here
# only, so editing the eval's definition is a single-file change and
# the Eval Object version bumps atomically with the scaffold edits.
#
# Wire-level points worth knowing:
#
# - *`/obj/create` with `builtin_object_class="Evaluation"`* is the
#   cookbook's chosen path (matching the SDK). The specialized
#   `POST /v2/.../evaluations` endpoint also exists but auto-creates
#   per-eval-aliased Ops (`<eval-id>.evaluate`) the cookbook doesn't
#   use — `/eval_results/query` filters by canonical name, not
#   per-eval-aliased name. ADR-0005 (lands with recipe 12) captures
#   this decision in detail.
# - *Why `/file/create` + `/obj/create` for the Ops, not `/v2/.../ops`?*
#   The `/v2/.../ops` endpoint lowercases `object_id`
#   (`Evaluation.evaluate` -> `evaluation.evaluate`) — and
#   `/eval_results/query` filters on the exact capital-case names.
#   The SDK uses `/file/create` (multipart) to upload the source and
#   `/obj/create` to wrap it as a `kind="op"` Object — that path
#   preserves case. The cookbook follows suit. The Op's val mirrors
#   the SDK shape:
#       {"_type" => "CustomWeaveType",
#        "files" => {"obj.py" => "<file digest>"},
#        "weave_type" => {"type" => "Op"}}
# - The Eval Object val mirrors the SDK shape: `_bases=["Object",
#   "BaseModel"]`, `_class_name="Evaluation"`, `_type="Evaluation"`,
#   plus the field refs (dataset, evaluate, predict_and_score,
#   summarize, scorers, trials). Per-language identity comes from a
#   per-language `object_id` (`recipe-11-eval-<lang>`); canonical Op
#   names stay shared because the aggregator's filter requires it.
# - Tags + aliases (recipe 07's pattern) apply here too — tagging the
#   Eval Object with environment / language gives UI-visible labels on
#   the Evaluation Definitions page.
#
# Run:
#   ruby ruby/11_create_evaluation.rb

require "digest"
require "json"
require "net/http"
require "securerandom"
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

RECIPE_PATH = "ruby/11_create_evaluation.rb"
RECIPE_SHA = Digest::SHA256.hexdigest(File.read(__FILE__))[0, 16]


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

def upload_op_source(source)
  # Upload Op source as a file (multipart) and return the file digest.
  # /file/create is the ONE multipart endpoint the cookbook uses; every
  # other endpoint takes JSON. The returned digest goes into the Op's
  # val under `files.obj.py`.
  uri = URI.join(BASE_URL, "/file/create")
  boundary = "----CookbookBoundary#{SecureRandom.hex(16)}"
  body = +""
  body << "--#{boundary}\r\n"
  body << "Content-Disposition: form-data; name=\"project_id\"\r\n\r\n"
  body << "#{PROJECT_ID}\r\n"
  body << "--#{boundary}\r\n"
  body << "Content-Disposition: form-data; name=\"file\"; filename=\"obj.py\"\r\n"
  body << "Content-Type: application/octet-stream\r\n\r\n"
  body << source
  body << "\r\n--#{boundary}--\r\n"

  req = Net::HTTP::Post.new(uri, "Content-Type" => "multipart/form-data; boundary=#{boundary}")
  req.basic_auth("api", API_KEY)
  req.body = body
  res = Net::HTTP.start(uri.host, uri.port, use_ssl: uri.scheme == "https") { |http| http.request(req) }
  abort "HTTP #{res.code} for /file/create: #{res.body}" unless res.code.start_with?("2")
  JSON.parse(res.body).fetch("digest")
end

def latest_object(object_id)
  body = post_json("/objs/query", {
    "project_id" => PROJECT_ID,
    "filter" => { "object_ids" => [object_id], "latest_only" => true },
    "metadata_only" => true,
  })
  body["objs"]&.first
end

def latest_dataset_by_prefix(prefix)
  # Recipe 10 timestamps Dataset names so exact lookup won't work — list
  # Datasets sorted desc by created_at and pick the first prefix match.
  body = post_json("/objs/query", {
    "project_id" => PROJECT_ID,
    "filter" => { "base_object_classes" => ["Dataset"] },
    "sort_by" => [{ "field" => "created_at", "direction" => "desc" }],
    "limit" => 50,
    "metadata_only" => true,
  })
  (body["objs"] || []).find { |o| o["object_id"].start_with?(prefix) }
end


# 1) Look up the prerequisites from earlier recipes. Abort with a clear
# pointer to the recipe that would create the missing artifact.
model = latest_object("recipe-08-model-ruby")
abort "FAIL: model `recipe-08-model-ruby` not found. Run ruby/08_use_model.rb first." if model.nil?
puts "Found:     model    #{model["object_id"]} digest=#{model["digest"][0, 12]}…"

scorer = latest_object("recipe-09-is-correct-ruby")
abort "FAIL: scorer `recipe-09-is-correct-ruby` not found. Run ruby/09_score_a_call.rb first." if scorer.nil?
puts "Found:     scorer   #{scorer["object_id"]} digest=#{scorer["digest"][0, 12]}…"

dataset = latest_dataset_by_prefix("recipe-10-dataset-ruby")
abort "FAIL: no Dataset matching `recipe-10-dataset-ruby-*` found. Run ruby/10_create_dataset.rb first." if dataset.nil?
puts "Found:     dataset  #{dataset["object_id"]} digest=#{dataset["digest"][0, 12]}…"


# 2) Register the three canonical Eval Ops with ADR-0004 scaffolds.
# Content-addressed: re-running an unchanged recipe is a no-op (same
# digest stays); editing this recipe bumps version_index and (in
# step 3) bumps the Eval Object too.
def scaffold(op_name, signature, body_doc)
  <<~PY
    # Cookbook scaffold (ruby)
    # Source: #{RECIPE_PATH}
    # SHA256: #{RECIPE_SHA}

    import weave


    @weave.op
    def #{signature}:
        """#{body_doc}

        Byte-for-byte reference (SHA256 of the recipe file):
            #{RECIPE_SHA}

        To verify a local copy of the file matches (POSIX shell):
            shasum -a 256 #{RECIPE_PATH} | cut -c1-16

        Canonical lifecycle-marker Op for the cookbook's eval flow. The
        W&B service identifies this Op by `object_id` (#{op_name.inspect})
        and uses it to recognise the structured Call trace recipe 12
        builds. The body raises NotImplementedError by design — real
        eval logic lives client-side in recipe 12.
        """
        raise NotImplementedError(
            "This op is a Python scaffold uploaded from a non-Python recipe. "
            "See the docstring above for the real source-language file and a "
            "verifiable byte-for-byte reference (SHA256)."
        )
  PY
end

CANONICAL_OPS = {
  "Evaluation.evaluate" => scaffold(
    "Evaluation.evaluate",
    "evaluate(self, model)",
    "Root of an evaluation Call trace. Wraps one full pass over\n        the dataset with the given model + scorers.",
  ),
  "Evaluation.predict_and_score" => scaffold(
    "Evaluation.predict_and_score",
    "predict_and_score(self, example)",
    "Per-row child of the eval root. One trial = one dataset row\n        scored by all configured scorers.",
  ),
  "Evaluation.summarize" => scaffold(
    "Evaluation.summarize",
    "summarize(self, eval_table)",
    "Final sibling of predict_and_score children under the root.\n        Aggregates per-row scorer outputs into evaluation-level stats.",
  ),
}.freeze

eval_op_refs = {}
CANONICAL_OPS.each do |op_id, source|
  file_digest = upload_op_source(source)
  res = post_json("/obj/create", {
    "obj" => {
      "project_id" => PROJECT_ID,
      "object_id" => op_id,
      "val" => {
        "_type" => "CustomWeaveType",
        "files" => { "obj.py" => file_digest },
        "weave_type" => { "type" => "Op" },
      },
    },
  })
  eval_op_refs[op_id] = "weave:///#{PROJECT_ID}/op/#{res["object_id"]}:#{res["digest"]}"
  puts "Op:        #{res["object_id"]} digest=#{res["digest"][0, 12]}… (file=#{file_digest[0, 12]}…)"
end


# 3) Build the Evaluation Object. The val mirrors the SDK shape: each
# canonical Op is a structured `method` field on the object (so the
# W&B UI can render them inline on the Eval Definitions page), and
# `scorers` is a list of Op refs.
def obj_ref(o)
  "weave:///#{PROJECT_ID}/object/#{o["object_id"]}:#{o["digest"]}"
end

def op_ref(o)
  "weave:///#{PROJECT_ID}/op/#{o["object_id"]}:#{o["digest"]}"
end

eval_object_id = "recipe-11-eval-ruby"
eval_val = {
  "_bases" => ["Object", "BaseModel"],
  "_class_name" => "Evaluation",
  "_type" => "Evaluation",
  "name" => eval_object_id,
  "description" => "Cookbook evaluation definition (ruby recipe 11)",
  "dataset" => obj_ref(dataset),
  "evaluate" => eval_op_refs["Evaluation.evaluate"],
  "predict_and_score" => eval_op_refs["Evaluation.predict_and_score"],
  "summarize" => eval_op_refs["Evaluation.summarize"],
  "scorers" => [op_ref(scorer)],
  "trials" => 1,
  "evaluation_name" => nil,
  "metadata" => nil,
  "preprocess_model_input" => nil,
}
created = post_json("/obj/create", {
  "obj" => {
    "project_id" => PROJECT_ID,
    "object_id" => eval_object_id,
    "val" => eval_val,
    "builtin_object_class" => "Evaluation",
  },
})
eval_digest = created.fetch("digest")
eval_ref = "weave:///#{PROJECT_ID}/object/#{eval_object_id}:#{eval_digest}"
puts "Published: #{eval_object_id} digest=#{eval_digest[0, 12]}…"
puts "  ref: #{eval_ref}"


# 4) Tag + alias (recipe 07's pattern). Tags are per-version, additive,
# UI-visible labels; aliases are per-object_id named pointers.
env_tag = ENV.fetch("COOKBOOK_ENVIRONMENT", "dev")
tags_to_add = [env_tag, "ruby"]
put_json("/objs/#{eval_object_id}/versions/#{eval_digest}/tags", {
  "project_id" => PROJECT_ID,
  "tags" => tags_to_add,
})
puts "Tagged:    #{tags_to_add.inspect} -> version #{eval_digest[0, 12]}…"

aliases_to_set = ["staging"]
put_json("/objs/#{eval_object_id}/aliases", {
  "project_id" => PROJECT_ID,
  "digest" => eval_digest,
  "aliases" => aliases_to_set,
})
puts "Aliased:   #{aliases_to_set.inspect} -> version #{eval_digest[0, 12]}…"


# --- verification ---
# Read the Eval Object back (with tags + aliases) and assert every ref
# + metadata field round-trips. Brief retry for read-after-write lag.
read_back = nil
8.times do
  body = post_json("/obj/read", {
    "project_id" => PROJECT_ID,
    "object_id" => eval_object_id,
    "digest" => eval_digest,
    "include_tags_and_aliases" => true,
  })
  read_back = body["obj"]
  # Retry until the obj is visible AND tags + aliases have propagated.
  # /obj/create returns synchronously but tags / aliases land via a
  # separate propagation path; reading the obj before they catch up
  # is racy.
  if read_back
    tags_now = read_back["tags"] || []
    aliases_now = read_back["aliases"] || []
    break if tags_to_add.all? { |t| tags_now.include?(t) } && aliases_to_set.all? { |a| aliases_now.include?(a) }
  end

  sleep 1
end
abort "FAIL: Eval Object #{eval_object_id}:#{eval_digest} not fully visible (tags=#{(read_back && read_back["tags"]).inspect} aliases=#{(read_back && read_back["aliases"]).inspect}) after 8 reads" if read_back.nil?

val = read_back["val"]
abort "_class_name: #{val["_class_name"].inspect}" unless val["_class_name"] == "Evaluation"
abort "dataset: #{val["dataset"].inspect}" unless val["dataset"] == obj_ref(dataset)
abort "evaluate: #{val["evaluate"].inspect}" unless val["evaluate"] == eval_op_refs["Evaluation.evaluate"]
abort "predict_and_score: #{val["predict_and_score"].inspect}" unless val["predict_and_score"] == eval_op_refs["Evaluation.predict_and_score"]
abort "summarize: #{val["summarize"].inspect}" unless val["summarize"] == eval_op_refs["Evaluation.summarize"]
abort "scorers: #{val["scorers"].inspect}" unless val["scorers"] == [op_ref(scorer)]
abort "trials: #{val["trials"].inspect}" unless val["trials"] == 1
abort "base_object_class: #{read_back["base_object_class"].inspect}" unless read_back["base_object_class"] == "Evaluation"
tags = read_back["tags"] || []
aliases = read_back["aliases"] || []
tags_to_add.each do |t|
  abort "tag #{t.inspect} missing from #{tags.inspect}" unless tags.include?(t)
end
aliases_to_set.each do |a|
  abort "alias #{a.inspect} missing from #{aliases.inspect}" unless aliases.include?(a)
end
puts "Verified:  Eval Object refs + tags + aliases round-trip (tags=#{tags.inspect}, aliases=#{aliases.inspect})"
