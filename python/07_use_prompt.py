# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 07: publish a Prompt + reference it from a Call + tag/alias it.

Introduces four new things that recipes 08-13 build on:

    POST /obj/create                            -> generic Weave Object
                                                   endpoint; here, publish
                                                   a StringPrompt
    POST /obj/read                              -> read it back
    PUT  /objs/{id}/versions/{digest}/tags      -> add version tags
    PUT  /objs/{id}/aliases                     -> set named pointers

    (and the existing /call/start + /call/end, but now with
     `inputs.prompt` = a weave:// ref to the Prompt — the "object ref
     in trace inputs" pattern that unlocks Model.predict, Scorer Ops,
     and the eval flow)

Five wire-level points worth knowing:

- The Object endpoint is **flat under an `obj` wrapper**:
      {"obj": {"project_id", "object_id", "val"}}
  The val you submit is what Weave stores verbatim (after lowercasing
  the `object_id`). The val MUST carry `_bases`, `_class_name`, and
  `_type` for the Weave UI to recognise the object — the server does
  not auto-fill these. An optional `builtin_object_class` field on the
  request must match val's `_class_name` exactly when set; omitting it
  is cleaner (the val is the single source of truth on class info).
- `base_object_class="Prompt"` (what the W&B UI's Prompts page filters
  on) is derived by the server from `val._bases`; `leaf_object_class`
  comes from `val._class_name`. A one-line variant for messages-shaped
  prompts is `MessagesPrompt` (list of `{role, content}` dicts) — not
  demonstrated here, but the same val shape applies (`_class_name` /
  `_type` become `"MessagesPrompt"`, and a `messages` field replaces
  `content`).
- A Prompt is content-addressed: identical val collapses to the same
  `(digest, version_index)`. Editing the content (or any other val
  field) bumps the version. No timestamping needed; this recipe's
  per-language identity comes from a different `object_id` per port.
- **Tags vs aliases** — both UI-visible Object metadata, separate from
  val (so changing them does NOT bump the version):
    * Tags are per-VERSION, additive labels (e.g., "dev", "production",
      "reviewed"). PUT adds, POST .../remove removes. Many versions can
      share a tag.
    * Aliases are per-object_id named pointers — re-PUTting an alias
      detaches it from the prior version. The server auto-maintains a
      `latest` alias on the most-recent version; do not set it yourself.
  These same endpoints apply to any Weave Object (Model, Dataset,
  Evaluation, Scorer Op), not just Prompts.
- **Val "extras"** — you can also stuff arbitrary JSON fields directly
  into val (any type, nested dicts, etc.) alongside the canonical
  `content`/`description`/`name`. They round-trip cleanly and are
  queryable via /objs/query filters, but DO NOT appear in dedicated UI
  columns or panels — only `tags` and `aliases` do. Use val extras for
  structured machine-queryable metadata; use tags/aliases for UI-visible
  labels and pointers.

Run:
    uv run python/07_use_prompt.py
"""
import os
import sys
import time
from datetime import datetime, timezone

import requests

BASE_URL = os.environ.get("WEAVE_SERVICE_URL", "https://trace.wandb.ai")

required = ["WANDB_API_KEY", "WANDB_ENTITY", "WANDB_PROJECT"]
missing = [k for k in required if not os.environ.get(k)]
if missing:
    sys.exit(f"Missing required env vars: {', '.join(missing)}. See ../README.md#setup.")

PROJECT_ID = f"{os.environ['WANDB_ENTITY']}/{os.environ['WANDB_PROJECT']}"
AUTH = ("api", os.environ["WANDB_API_KEY"])


def post(path: str, body: dict) -> dict:
    r = requests.post(f"{BASE_URL}{path}", auth=AUTH, json=body)
    r.raise_for_status()
    return r.json()


def put(path: str, body: dict) -> dict:
    r = requests.put(f"{BASE_URL}{path}", auth=AUTH, json=body)
    r.raise_for_status()
    return r.json()


# 1) Publish a StringPrompt via the generic Object endpoint.
# The val mirrors what the SDK produces for `weave.StringPrompt(content=...)`.
#
# val "extras": you could add arbitrary JSON fields here alongside the
# canonical ones below (e.g., "owner_email": "alice@example.com",
# "model_target": "gpt-4o-mini", "custom_attributes": {...}). They'd
# round-trip cleanly and be queryable via /objs/query filters, but
# would NOT appear in dedicated UI columns. For UI-visible metadata,
# use the tags + aliases steps further down.
prompt_object_id = "recipe-07-prompt-python"
prompt_val = {
    "_bases": ["Prompt", "Object", "BaseModel"],
    "_class_name": "StringPrompt",
    "_type": "StringPrompt",
    "name": None,
    "description": "Capital-city Q&A prompt template (python recipe 07)",
    "content": "Answer the question concisely: {question}",
}
created = post("/obj/create", {
    "obj": {
        "project_id": PROJECT_ID,
        "object_id": prompt_object_id,
        "val": prompt_val,
    }
})
prompt_digest = created["digest"]
prompt_ref = f"weave:///{PROJECT_ID}/object/{prompt_object_id}:{prompt_digest}"
print(f"Published: {prompt_object_id} digest={prompt_digest[:12]}…")
print(f"  ref: {prompt_ref}")

# 2) Tag this version with the current cookbook environment ("dev" or
# "ci"). Tags are a first-class, per-version, UI-visible metadata
# channel — separate from val. PUT is additive (re-runs are no-ops if
# the tag is already present); removal uses POST /objs/.../tags/remove
# with the same body shape. The same endpoint applies to any Weave
# Object (Model, Dataset, Evaluation, Scorer Op).
env_tag = os.environ.get("COOKBOOK_ENVIRONMENT", "dev")
tags_to_add = [env_tag, "python"]
put(f"/objs/{prompt_object_id}/versions/{prompt_digest}/tags", {
    "project_id": PROJECT_ID,
    "tags": tags_to_add,
})
print(f"Tagged:    {tags_to_add} -> version {prompt_digest[:12]}…")

# 3) Set named aliases pointing at this version. Aliases are
# per-object_id named pointers — re-PUTting any alias later on a
# different version detaches it from this one. Typical examples are
# deployment targets ("staging", "production") and release candidates
# ("v1-candidate"). The server also auto-maintains a `latest` alias
# on the most-recent version; do not try to set "latest" yourself.
aliases_to_set = ["staging", "v1-candidate"]
put(f"/objs/{prompt_object_id}/aliases", {
    "project_id": PROJECT_ID,
    "digest": prompt_digest,
    "aliases": aliases_to_set,
})
print(f"Aliased:   {aliases_to_set} -> version {prompt_digest[:12]}…")

# 4) Read it back (with tags + aliases) and assert everything
# round-trips. The read response carries the version_index the
# create response omits.
read_back = post("/obj/read", {
    "project_id": PROJECT_ID,
    "object_id": prompt_object_id,
    "digest": prompt_digest,
    "include_tags_and_aliases": True,
})
obj = read_back["obj"]
assert obj["val"]["_class_name"] == "StringPrompt", f"_class_name: {obj['val']['_class_name']!r}"
assert obj["val"]["content"] == prompt_val["content"], f"content: {obj['val']['content']!r}"
assert obj["base_object_class"] == "Prompt", f"base_object_class: {obj['base_object_class']!r}"
assert obj["leaf_object_class"] == "StringPrompt", f"leaf_object_class: {obj['leaf_object_class']!r}"
tags = obj.get("tags") or []
aliases = obj.get("aliases") or []
for expected_tag in tags_to_add:
    assert expected_tag in tags, f"tag {expected_tag!r} missing from {tags!r}"
for expected_alias in aliases_to_set:
    assert expected_alias in aliases, f"alias {expected_alias!r} missing from {aliases!r}"
print(f"Read:      version={obj['version_index']} tags={tags} aliases={aliases}")

# 3) Open a Call whose `inputs.prompt` is the Prompt's weave:// ref —
# the "object ref in trace inputs" pattern. The UI will follow this
# ref and render the prompt content inline in the call view.
question = "What is the capital of France?"
r = post("/call/start", {
    "start": {
        "project_id": PROJECT_ID,
        "op_name": "recipe-07-prompt-in-trace",
        "started_at": datetime.now(timezone.utc).isoformat(),
        "attributes": {
            "cookbook.language": "python",
            "cookbook.recipe": "07_use_prompt",
            "cookbook.environment": os.environ.get("COOKBOOK_ENVIRONMENT", "dev"),
        },
        "inputs": {"prompt": prompt_ref, "question": question},
    }
})
call_id = r["id"]
trace_id = r["trace_id"]
print(f"Started:   id={call_id} (inputs.prompt = {prompt_ref})")

# Client-side: substitute the question into the prompt template.
# (We could also leave the substitution to a downstream model — recipe
# 08 does that. For recipe 07 we keep things minimal.)
rendered = prompt_val["content"].format(question=question)
answer = "Paris"

post("/call/end", {
    "end": {
        "project_id": PROJECT_ID,
        "id": call_id,
        "ended_at": datetime.now(timezone.utc).isoformat(),
        "summary": {},
        "output": {"rendered_prompt": rendered, "answer": answer},
    }
})
print(f"Ended:     id={call_id} output.answer={answer!r}")

# --- verification ---
# Read the Call back and assert inputs.prompt round-trips as the same
# weave:// URI we sent. Brief retry tolerates read-after-write lag.
call = None
for _ in range(5):
    r = post("/call/read", {"project_id": PROJECT_ID, "id": call_id})
    call = r.get("call")
    if call and call.get("ended_at"):
        break
    time.sleep(1)
else:
    sys.exit(f"FAIL: Call {call_id} not visible/finished after 5 reads")

assert call["inputs"]["prompt"] == prompt_ref, f"inputs.prompt: {call['inputs']['prompt']!r}"
assert call["inputs"]["question"] == question, f"inputs.question: {call['inputs']['question']!r}"
assert call["output"]["answer"] == answer, f"output.answer: {call['output']['answer']!r}"
assert call["trace_id"] == trace_id, f"trace_id: {call['trace_id']!r}"
print(f"Verified:  prompt ref round-trips in call inputs")
