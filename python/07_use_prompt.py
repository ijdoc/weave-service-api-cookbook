# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 07: publish a Prompt + reference it from a Call.

Introduces two new things that recipes 08-13 build on:

    POST /obj/create   -> the generic Weave Object endpoint, used here
                          to publish a StringPrompt
    POST /obj/read     -> read it back by (object_id, digest)

    (and the existing /call/start + /call/end, but now with
     `inputs.prompt` = a weave:// ref to the Prompt — the "object ref
     in trace inputs" pattern that unlocks Model.predict, Scorer Ops,
     and the eval flow)

Three wire-level points worth knowing:

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


# 1) Publish a StringPrompt via the generic Object endpoint.
# The val mirrors what the SDK produces for `weave.StringPrompt(content=...)`.
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

# 2) Read it back and assert the val + derived class fields round-trip.
# The read response carries the version_index the create response omits.
read_back = post("/obj/read", {
    "project_id": PROJECT_ID,
    "object_id": prompt_object_id,
    "digest": prompt_digest,
})
obj = read_back["obj"]
assert obj["val"]["_class_name"] == "StringPrompt", f"_class_name: {obj['val']['_class_name']!r}"
assert obj["val"]["content"] == prompt_val["content"], f"content: {obj['val']['content']!r}"
assert obj["base_object_class"] == "Prompt", f"base_object_class: {obj['base_object_class']!r}"
assert obj["leaf_object_class"] == "StringPrompt", f"leaf_object_class: {obj['leaf_object_class']!r}"
print(f"Read:      version={obj['version_index']} base_object_class={obj['base_object_class']!r} leaf_object_class={obj['leaf_object_class']!r}")

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
