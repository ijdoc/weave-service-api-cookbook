# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 08: create a versioned Model + use it in a trace.

First application of **ADR-0004** (the source-embedding scaffold). The
recipe creates two Weave Objects:

    POST /v2/{entity}/{project}/ops   -> register the predict Op
                                         (Python scaffold per ADR-0004)
    POST /obj/create                  -> register the Model object,
                                         pointing val.predict at the
                                         predict Op's weave:// ref

Then it opens a Call that references both — establishing the
"predict logic lives in the recipe file; Weave records identity +
invocation" pattern that recipes 09–12 reuse.

Three wire-level points worth knowing:

- **The Model is created via `/obj/create`, NOT `/v2/.../models`.**
  The specialized endpoint stashes the entire source into
  `files.obj.py` as a single "code tab" attachment and does NOT add
  per-method ref fields. The W&B UI's Model page renders methods
  inline only when the val carries a `<method>: <weave:// op ref>`
  field. The SDK uses the generic Object endpoint with structured
  metadata for exactly this reason; the cookbook follows suit.
- The Model val mirrors the SDK shape: `_bases=["Model", "Object",
  "BaseModel"]`, `_class_name=<subclass>`, `_type=<subclass>`, a
  `predict` field pointing at the predict Op's weave:// ref, plus
  **instance attributes that represent the model's instantiation
  config**. Realistic attributes here are `model_name`, `temperature`,
  `max_tokens` — the values that distinguish one Model version from
  another. **Per-Call data** like the question being asked and the
  answer returned live in the Call's inputs / output, NOT on the
  Model. Editing a Model attribute is a versioning event; logging a
  new Call is not.
- The UI's CallPage parses `op_name` and `inputs.self` as weave://
  URIs and crashes on raw strings — both MUST be real refs.

Editing this file changes its SHA256 → the Op scaffold changes →
Weave bumps the predict Op's `version_index`. Per-language identity
comes from the Model + Op object_ids (`recipe-08-model-<lang>` and
`recipe-08-model-<lang>.predict`).

For brevity this recipe mocks the actual LLM invocation — the Call's
output is a hardcoded answer. A real recipe would call the LLM named
in `model_name` with the Model's `temperature` / `max_tokens`
settings and the rendered prompt (recipe 07 covers prompts), then
surface the response.

Run:
    uv run python/08_use_model.py
"""
import hashlib
import os
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import requests

BASE_URL = os.environ.get("WEAVE_SERVICE_URL", "https://trace.wandb.ai")

required = ["WANDB_API_KEY", "WANDB_ENTITY", "WANDB_PROJECT"]
missing = [k for k in required if not os.environ.get(k)]
if missing:
    sys.exit(f"Missing required env vars: {', '.join(missing)}. See ../README.md#setup.")

ENTITY = os.environ["WANDB_ENTITY"]
PROJECT = os.environ["WANDB_PROJECT"]
PROJECT_ID = f"{ENTITY}/{PROJECT}"
AUTH = ("api", os.environ["WANDB_API_KEY"])

# --- ADR-0004 scaffold for the predict Op ---
# SHA256 of this recipe file's bytes. Edits flow through to OP_SOURCE
# below, which is what Weave content-addresses on. Re-running an
# unchanged file is idempotent; editing bumps the predict Op version.
RECIPE_PATH = "python/08_use_model.py"
RECIPE_SHA = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()[:16]

OP_SOURCE = f'''# Cookbook scaffold (python)
# Source: {RECIPE_PATH}
# SHA256: {RECIPE_SHA}

import weave


@weave.op
def predict(self, question):
    """The actual predict implementation lives in:
        {RECIPE_PATH}

    Byte-for-byte reference (SHA256 of the recipe file):
        {RECIPE_SHA}

    This Python op is a metadata handle, not the real model — running
    it raises NotImplementedError by design.
    """
    raise NotImplementedError(
        "This op is a Python scaffold uploaded from a non-Python recipe. "
        "See the docstring above for the real source-language file and a "
        "verifiable byte-for-byte reference (SHA256)."
    )
'''


def post(path: str, body: dict) -> dict:
    r = requests.post(f"{BASE_URL}{path}", auth=AUTH, json=body)
    r.raise_for_status()
    return r.json()


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


# 1) Register the predict Op via the specialized /v2/.../ops endpoint.
# Object_id is `<ClassName>.predict` by convention; the server
# lowercases it. The Op carries the ADR-0004 scaffold as its source.
op_name = "recipe-08-model-python.predict"
op_res = post(
    f"/v2/{ENTITY}/{PROJECT}/ops",
    {"name": op_name, "source_code": OP_SOURCE},
)
predict_op_ref = f"weave:///{PROJECT_ID}/op/{op_res['object_id']}:{op_res['digest']}"
print(f"Predict op: {op_res['object_id']} digest={op_res['digest'][:12]}… version={op_res['version_index']}")

# 2) Register the Model via the generic /obj/create endpoint.
# The val mirrors the SDK's Model shape — `_bases` lists the MRO
# starting at "Model", `_class_name`/_type is a real subclass name,
# and `predict` is a weave:// ref to the Op we just registered.
# This is what makes the W&B UI render the predict source inline on
# the Model page (rather than tucking it into a separate "code" tab).
model_object_id = "recipe-08-model-python"
# Instance attributes here are the kind of config a real Model would
# carry — change any value and you get a new (digest, version_index).
# Q&A specifics (the question, the answer) belong on the Call, not the
# Model.
model_val = {
    "_bases": ["Model", "Object", "BaseModel"],
    "_class_name": "Recipe08PythonModel",
    "_type": "Recipe08PythonModel",
    "name": model_object_id,
    "description": "Cookbook model handle (python recipe 08)",
    "model_name": "gpt-4o-mini",
    "temperature": 0.7,
    "max_tokens": 100,
    "predict": predict_op_ref,
}
model_res = post("/obj/create", {
    "obj": {
        "project_id": PROJECT_ID,
        "object_id": model_object_id,
        "val": model_val,
    }
})
model_digest = model_res["digest"]
model_ref = f"weave:///{PROJECT_ID}/object/{model_object_id}:{model_digest}"
print(f"Model:      {model_res['object_id']} digest={model_digest[:12]}…")
print(f"  ref: {model_ref}")

# 3) Open a Call that uses the predict Op + Model. The op_name MUST
# be the Op ref (not a bare string), and inputs.self MUST be the
# Model ref — both are what the UI's CallPage parses as weave:// URIs.
question = "Is the sky blue?"
r = post(
    "/call/start",
    {
        "start": {
            "project_id": PROJECT_ID,
            "op_name": predict_op_ref,
            "started_at": now(),
            "attributes": {
                "cookbook.language": "python",
                "cookbook.recipe": "08_use_model",
                "cookbook.environment": os.environ.get("COOKBOOK_ENVIRONMENT", "dev"),
            },
            "inputs": {"self": model_ref, "question": question},
        }
    },
)
call_id = r["id"]
trace_id = r["trace_id"]
print(f"Started:    id={call_id}")

# 4) End the Call with the model's answer.
# A real recipe would call `model_val["model_name"]` here with the
# question and the model's temperature/max_tokens settings, and use
# the LLM's response as the Call's output. We hardcode an answer so
# the cookbook stays focused on the wire-level Model + Op + Call
# wiring.
answer = "yes"
post(
    "/call/end",
    {
        "end": {
            "project_id": PROJECT_ID,
            "id": call_id,
            "ended_at": now(),
            "summary": {
                "status_counts": {"success": 1, "error": 0},
                "weave": {"status": "success", "trace_name": op_name},
            },
            "output": answer,
        }
    },
)
print(f"Ended:      id={call_id} output={answer!r}")

# --- verification ---
# Read the Call back and assert the model + op linkage round-trips.
call = None
for _ in range(5):
    r = post("/call/read", {"project_id": PROJECT_ID, "id": call_id})
    call = r.get("call")
    if call and call.get("ended_at"):
        break
    time.sleep(1)
else:
    sys.exit(f"FAIL: Call {call_id} not visible/finished after 5 reads")

assert call["op_name"] == predict_op_ref, f"op_name: {call['op_name']!r}"
assert call["inputs"]["self"] == model_ref, f"inputs.self: {call['inputs']['self']!r}"
assert call["inputs"]["question"] == question, f"inputs.question: {call['inputs']['question']!r}"
assert call["output"] == answer, f"output: {call['output']!r}"
assert call["trace_id"] == trace_id, f"trace_id: {call['trace_id']!r}"
print(f"Verified:   id={call_id} (op + model + output round-tripped)")
