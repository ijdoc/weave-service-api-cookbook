# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 09: create a Scorer Op + score a Call (the apply_scorer pattern).

Wire-level equivalent of the SDK's `result.call.apply_scorer(scorer)`
pattern — score an arbitrary already-logged Call without dragging in
the full evaluation flow (recipes 11-13). Reuses the ADR-0004
Op-creation pattern from recipe 08, this time for a scorer function.

A **Scorer Op** is just an Op whose role is to score a Call's output.
There is no separate Scorer Object class to register here — the W&B
service does expose `POST /v2/.../scorers` (a dedicated Scorer object
endpoint), but the cookbook does not use it; the Op pattern is what
`@weave.op` scorer functions use and what `apply_scorer` integrates
with under the hood.

This recipe builds three things on the wire:

1. A small model Call producing a sample prediction (mirrors recipe
   08's predict shape but simpler — we skip the Model object and the
   predict Op, just open a Call directly).
2. A scoring Call invoking the Scorer Op, with the prediction +
   expected answer as inputs and the score value as output. This is
   a top-level standalone Call (no parent_id; separate trace) — same
   shape `apply_scorer` produces.
3. A **`wandb.runnable.<scorer_op_id>`** Feedback row attached to the
   prediction Call. **This Feedback is the load-bearing link that
   makes the score render inline under the prediction in the W&B UI.**
   Without it, the score Call would be a disconnected island.

Wire-level points worth knowing:

- The **`wandb.runnable.*`** Feedback convention is how SDK
  `apply_scorer` ties a standalone scoring Call back to a prediction
  Call. The Feedback row carries:
      feedback_type = "wandb.runnable.<scorer_op_id>"
      payload       = {"output": <score value>}
      runnable_ref  = <Scorer Op weave:// ref>
      call_ref      = <score Call weave:// ref>
  The UI reads `wandb.runnable.*` Feedbacks on the prediction Call
  and shows the score (plus a link to the score Call). This is the
  same Feedback endpoint family covered in recipes 05-06, just with
  a specific feedback_type pattern Weave recognises.
- Scorer-Op scoring (this recipe) and plain `feedback_type` scoring
  (recipe 06 — `wandb.note.1`, `wandb.reaction.1`, arbitrary user
  types) coexist. The structured eval flow (recipe 12) uses scorer
  Ops + nested children under `Evaluation.predict_and_score`, plus
  matching Feedback rows. Recipe 09 is the standalone apply-scorer-
  to-an-existing-call shape.
- Scorer Op object_ids are NOT aggregator-filtered, so per-language
  naming (`recipe-09-is-correct-{python,ruby,dotnet}`) is fine. The
  canonical Eval Op names in recipe 12 (`Evaluation.evaluate` etc.)
  *are* aggregator-filtered, which is why those stay shared.
- The Scorer Op's source carries the ADR-0004 scaffold (header +
  in-method docstring + raise NotImplementedError + shasum verify
  hint).

Run:
    uv run python/09_score_a_call.py
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

# --- ADR-0004 scaffold for the Scorer Op ---
RECIPE_PATH = "python/09_score_a_call.py"
RECIPE_SHA = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()[:16]

SCORER_SOURCE = f'''# Cookbook scaffold (python)
# Source: {RECIPE_PATH}
# SHA256: {RECIPE_SHA}

import weave


@weave.op
def is_correct(output, expected):
    """The actual scoring implementation lives in:
        {RECIPE_PATH}

    Byte-for-byte reference (SHA256 of the recipe file):
        {RECIPE_SHA}

    To verify a local copy of the file matches (POSIX shell):
        shasum -a 256 {RECIPE_PATH} | cut -c1-16

    This Python op is a metadata handle, not the real scorer — running
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


# 1) Register the Scorer Op. Per-language object_id; the server
# lowercases it. Per the docstring, Scorer Op names are not
# aggregator-filtered, so per-language identity is fine.
scorer_op_id = "recipe-09-is-correct-python"
scorer_res = post(
    f"/v2/{ENTITY}/{PROJECT}/ops",
    {"name": scorer_op_id, "source_code": SCORER_SOURCE},
)
scorer_op_ref = f"weave:///{PROJECT_ID}/op/{scorer_res['object_id']}:{scorer_res['digest']}"
print(f"Scorer op:  {scorer_res['object_id']} digest={scorer_res['digest'][:12]}… version={scorer_res['version_index']}")

# 2) Produce a sample prediction via a tiny model Call. We deliberately
# don't recreate the full Model+predict-Op machinery from recipe 08
# here — this recipe's focus is scoring, not modelling. Real recipes
# would chain a recipe-08-style model Call with a recipe-09-style
# scoring Call, both under a recipe-12-style evaluation trace.
question = "Is the sky blue?"
expected = "yes"

# Open the prediction Call. op_name is a plain string for this minimal
# example (no model Op registered); inputs.question is raw.
r = post(
    "/call/start",
    {
        "start": {
            "project_id": PROJECT_ID,
            "op_name": "recipe-09-mock-predict",
            "started_at": now(),
            "attributes": {
                "cookbook.language": "python",
                "cookbook.recipe": "09_score_a_call",
                "cookbook.environment": os.environ.get("COOKBOOK_ENVIRONMENT", "dev"),
            },
            "inputs": {"question": question},
        }
    },
)
predict_call_id = r["id"]
trace_id = r["trace_id"]
prediction = "yes"
post(
    "/call/end",
    {
        "end": {
            "project_id": PROJECT_ID,
            "id": predict_call_id,
            "ended_at": now(),
            "summary": {
                "status_counts": {"success": 1, "error": 0},
                "weave": {"status": "success", "trace_name": "recipe-09-mock-predict"},
            },
            # Per the cookbook's question/answer convention (CONTRIBUTING.md),
            # predict outputs land under an `answer` key. The Scorer Op
            # below still takes the raw answer value as its `output`
            # argument — that's the scorer's signature, not the predict's
            # output shape.
            "output": {"answer": prediction},
        }
    },
)
print(f"Predicted:  id={predict_call_id} output={prediction!r}")

# 3) Open a scoring Call invoking the Scorer Op. op_name MUST be the
# Op's weave:// ref (not a bare string) for the UI to render the Op
# inline. Inputs are what's being scored (prediction + expected);
# output is the score value (boolean here — Eval Result aggregation
# in recipe 13 will classify this as a binary value type).
r = post(
    "/call/start",
    {
        "start": {
            "project_id": PROJECT_ID,
            "op_name": scorer_op_ref,
            "started_at": now(),
            "attributes": {
                "cookbook.language": "python",
                "cookbook.recipe": "09_score_a_call",
                "cookbook.environment": os.environ.get("COOKBOOK_ENVIRONMENT", "dev"),
            },
            "inputs": {"output": prediction, "expected": expected},
        }
    },
)
score_call_id = r["id"]
score = prediction == expected
post(
    "/call/end",
    {
        "end": {
            "project_id": PROJECT_ID,
            "id": score_call_id,
            "ended_at": now(),
            "summary": {
                "status_counts": {"success": 1, "error": 0},
                "weave": {"status": "success", "trace_name": scorer_op_id},
            },
            "output": score,
        }
    },
)
print(f"Scored:     id={score_call_id} output={score!r}")

# 4) Link the score to the prediction Call by creating a
# `wandb.runnable.<scorer_op_id>` Feedback row on the prediction.
# This is the load-bearing step — the W&B UI uses this Feedback (not
# any parent-child structure) to render the score inline on the
# prediction Call's view. The SDK's `apply_scorer` posts this exact
# shape under the hood.
predict_call_ref = f"weave:///{PROJECT_ID}/call/{predict_call_id}"
score_call_ref = f"weave:///{PROJECT_ID}/call/{score_call_id}"
feedback_res = post(
    "/feedback/create",
    {
        "project_id": PROJECT_ID,
        "weave_ref": predict_call_ref,
        "feedback_type": f"wandb.runnable.{scorer_op_id}",
        "payload": {"output": score},
        "runnable_ref": scorer_op_ref,
        "call_ref": score_call_ref,
    },
)
print(f"Linked:     feedback id={feedback_res['id']} on predict call (feedback_type=wandb.runnable.{scorer_op_id})")

# --- verification ---
# (a) The scoring Call round-trips with the right op_ref + inputs +
#     boolean output.
# (b) The wandb.runnable.* Feedback exists on the prediction Call
#     and carries the score value + scorer Op ref + score Call ref.
call = None
for _ in range(5):
    r = post("/call/read", {"project_id": PROJECT_ID, "id": score_call_id})
    call = r.get("call")
    if call and call.get("ended_at"):
        break
    time.sleep(1)
else:
    sys.exit(f"FAIL: scoring Call {score_call_id} not visible/finished after 5 reads")

assert call["op_name"] == scorer_op_ref, f"op_name: {call['op_name']!r}"
assert call["inputs"]["output"] == prediction, f"inputs.output: {call['inputs']['output']!r}"
assert call["inputs"]["expected"] == expected, f"inputs.expected: {call['inputs']['expected']!r}"
assert call["output"] == score, f"output: {call['output']!r}"

# Verify the wandb.runnable.* Feedback row exists on the prediction
# Call. /feedback/query filtered by weave_ref + feedback_type lands
# the same row we posted.
expected_feedback_type = f"wandb.runnable.{scorer_op_id}"
feedback_rows = None
for _ in range(5):
    r = post("/feedback/query", {
        "project_id": PROJECT_ID,
        "query": {"$expr": {"$eq": [
            {"$getField": "weave_ref"},
            {"$literal": predict_call_ref},
        ]}},
    })
    feedback_rows = r.get("result", [])
    if any(row["feedback_type"] == expected_feedback_type for row in feedback_rows):
        break
    time.sleep(1)
else:
    sys.exit(f"FAIL: no {expected_feedback_type!r} feedback on {predict_call_ref} after 5 reads")

linking = next(row for row in feedback_rows if row["feedback_type"] == expected_feedback_type)
assert linking["payload"] == {"output": score}, f"payload: {linking['payload']!r}"
assert linking["runnable_ref"] == scorer_op_ref, f"runnable_ref: {linking['runnable_ref']!r}"
assert linking["call_ref"] == score_call_ref, f"call_ref: {linking['call_ref']!r}"
print(f"Verified:   id={score_call_id} (scorer op + inputs + score output round-tripped)")
print(f"Verified:   wandb.runnable.{scorer_op_id} feedback links score -> predict")
