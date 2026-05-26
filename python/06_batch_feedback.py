# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 06: attach feedback to many Calls in one request.

Demonstrates the bulk variant of /feedback/create:
    POST /feedback/batch/create  -> N feedback items in one round trip

Two wire-level points worth knowing:

- The path is `/feedback/batch/create`, not the more guessable
  `/feedback/create-batch` or `/feedback/createBatch`.
- The body wraps a parallel-indexed array under `batch`:
      {"batch": [<FeedbackCreateReq>, <FeedbackCreateReq>, ...]}
  Each item carries its own `project_id`, `weave_ref`, `feedback_type`,
  and `payload` — exactly the shape /feedback/create takes. The
  response mirrors the input with `{"res": [<FeedbackCreateRes>, ...]}`,
  indices aligned to the input batch.

When to reach for batch over the per-item endpoint:

- Bulk-annotate a list of Calls after a review pass (this recipe's
  shape — one note per Call).
- Dump multiple feedback items at the end of a turn (scorer outputs,
  then notes, then ...).
- Anywhere round-trip count matters (many small items, latency-bound
  uploader).

This recipe creates three Calls and attaches **two feedback items per
Call** in a single batch request: a `wandb.note.1` (UI-visible in the
trace table) and a custom scorer-style feedback (queryable via
/feedback/query but not surfaced in the trace table). One round trip
ships 6 items; the same shape via per-item /feedback/create would
require 6 round trips.

This mirrors recipe 05's note + scorer split — same pair, but bulk.

Run:
    uv run python/06_batch_feedback.py
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

base_attributes = {
    "cookbook.language": "python",
    "cookbook.recipe": "06_batch_feedback",
    "cookbook.environment": os.environ.get("COOKBOOK_ENVIRONMENT", "dev"),
}
NOTE_TYPE = "wandb.note.1"
SCORER_TYPE = "recipe-06-scorer-correctness"


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def start_call(op_name: str, inputs: dict) -> str:
    r = requests.post(
        f"{BASE_URL}/call/start",
        auth=AUTH,
        json={
            "start": {
                "project_id": PROJECT_ID,
                "op_name": op_name,
                "started_at": now(),
                "attributes": base_attributes,
                "inputs": inputs,
            }
        },
    )
    r.raise_for_status()
    return r.json()["id"]


def end_call(call_id: str, output: dict) -> None:
    r = requests.post(
        f"{BASE_URL}/call/end",
        auth=AUTH,
        json={
            "end": {
                "project_id": PROJECT_ID,
                "id": call_id,
                "ended_at": now(),
                "summary": {},
                "output": output,
            }
        },
    )
    r.raise_for_status()


# Create three Calls — same shape as recipe 01, just repeated.
questions = [
    ("What is the capital of France?", "Paris"),
    ("What is the capital of Spain?", "Madrid"),
    ("What is the capital of Italy?", "Rome"),
]
calls: list[dict] = []
for i, (question, answer) in enumerate(questions, start=1):
    call_id = start_call(f"recipe-06-call-{i}", {"question": question})
    end_call(call_id, {"answer": answer})
    call_ref = f"weave:///{PROJECT_ID}/call/{call_id}"
    calls.append({"id": call_id, "ref": call_ref, "answer": answer})
    print(f"Call {i}: id={call_id}")

# Build the batch — note + scorer feedback per Call (6 items total).
batch = []
for call in calls:
    batch.append({
        "project_id": PROJECT_ID,
        "weave_ref": call["ref"],
        "feedback_type": NOTE_TYPE,
        "payload": {"note": f"Reviewed — answer: '{call['answer']}'"},
    })
    batch.append({
        "project_id": PROJECT_ID,
        "weave_ref": call["ref"],
        "feedback_type": SCORER_TYPE,
        "payload": {"output": {"score": 1.0, "reason": f"Answer '{call['answer']}' matches expected"}},
    })

# Single round trip for all three items.
r = requests.post(
    f"{BASE_URL}/feedback/batch/create",
    auth=AUTH,
    json={"batch": batch},
)
r.raise_for_status()
results = r.json()["res"]
assert len(results) == len(batch), f"batch size mismatch: sent {len(batch)} got {len(results)}"
for item, res in zip(batch, results):
    print(f"Batch->Feedback: type={item['feedback_type']} feedback_id={res['id']}")

# --- verification ---
# For each Call, query feedback by weave_ref and assert both the note
# and the scorer feedback landed with the expected payload. Brief retry
# tolerates eventual consistency in the read path.
expected_types = {NOTE_TYPE, SCORER_TYPE}
for call in calls:
    expected_note = {"note": f"Reviewed — answer: '{call['answer']}'"}
    expected_scorer = {"output": {"score": 1.0, "reason": f"Answer '{call['answer']}' matches expected"}}
    by_type: dict[str, dict] = {}
    for _ in range(5):
        r = requests.post(
            f"{BASE_URL}/feedback/query",
            auth=AUTH,
            json={
                "project_id": PROJECT_ID,
                "query": {
                    "$expr": {
                        "$eq": [
                            {"$getField": "weave_ref"},
                            {"$literal": call["ref"]},
                        ]
                    }
                },
            },
        )
        r.raise_for_status()
        rows = r.json().get("result", [])
        by_type = {row["feedback_type"]: row for row in rows if row["feedback_type"] in expected_types}
        if expected_types <= by_type.keys():
            break
        time.sleep(1)
    if not (expected_types <= by_type.keys()):
        sys.exit(f"FAIL: feedback for {call['ref']} not all visible after 5 reads (got {list(by_type)})")
    assert by_type[NOTE_TYPE]["payload"] == expected_note, (
        f"note payload for {call['id']}: {by_type[NOTE_TYPE]['payload']!r}"
    )
    assert by_type[SCORER_TYPE]["payload"] == expected_scorer, (
        f"scorer payload for {call['id']}: {by_type[SCORER_TYPE]['payload']!r}"
    )
    for row in by_type.values():
        assert row["weave_ref"] == call["ref"], f"weave_ref drift: {row['weave_ref']!r}"

print(f"Verified: {len(batch)} batched feedback items across {len(calls)} Calls (note + scorer each)")
