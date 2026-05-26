# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 05: attach feedback to a Call.

Demonstrates the feedback lifecycle:
    POST /feedback/create  -> attach feedback to a Call
    POST /feedback/query   -> read it back

Three wire-level points worth knowing:

- The Call is identified by `weave_ref`, not `call_id` directly:
      weave:///{entity}/{project}/call/{call_id}
  The recipe constructs this URI inline. There is also a `call_ref`
  field, but `weave_ref` is the required one.
- /feedback/create body is **flat** — top-level `project_id`,
  `weave_ref`, `feedback_type`, `payload` (no wrapper key, like
  /call/update; unlike /call/start and /call/end).
- /feedback/query uses the typed Query language. Filtering by
  `weave_ref` looks like:
      {"$expr": {"$eq": [
        {"$getField": "weave_ref"},
        {"$literal": "weave:///..."}
      ]}}

`feedback_type` is a freeform string. By convention:
- `wandb.<kind>.<version>` is reserved for W&B-recognized types that
  get UI treatment (e.g., `wandb.note.1`, `wandb.reaction.1`).
- Scorer-emitted feedback typically uses the scorer's name as a prefix
  so it's distinguishable from human annotation.

This recipe attaches one of each to the same Call to show the
many-to-one shape and the type-convention split.

Run:
    uv run python/05_add_feedback.py
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

op_name = "recipe-05-add-feedback"
attributes = {
    "cookbook.language": "python",
    "cookbook.recipe": "05_add_feedback",
    "cookbook.environment": os.environ.get("COOKBOOK_ENVIRONMENT", "dev"),
}
inputs = {"question": "What is the capital of Germany?"}
output = {"answer": "Berlin"}

# Two feedback items, illustrating the type-convention split.
human_note = {
    "feedback_type": "wandb.note.1",
    "payload": {"note": "Answer looks correct."},
}
scorer_feedback = {
    "feedback_type": "recipe-05-scorer-correctness",
    "payload": {"output": {"score": 1.0, "reason": "Answer matches expected"}},
}

# Open the Call.
r = requests.post(
    f"{BASE_URL}/call/start",
    auth=AUTH,
    json={
        "start": {
            "project_id": PROJECT_ID,
            "op_name": op_name,
            "started_at": datetime.now(timezone.utc).isoformat(),
            "attributes": attributes,
            "inputs": inputs,
        }
    },
)
r.raise_for_status()
call_id = r.json()["id"]
print(f"Started: id={call_id}")

# Close it.
r = requests.post(
    f"{BASE_URL}/call/end",
    auth=AUTH,
    json={
        "end": {
            "project_id": PROJECT_ID,
            "id": call_id,
            "ended_at": datetime.now(timezone.utc).isoformat(),
            "summary": {},
            "output": output,
        }
    },
)
r.raise_for_status()
print(f"Ended:   id={call_id}")

# Build the Call's weave_ref. /feedback/create takes this URI string,
# not a raw call_id.
call_ref = f"weave:///{PROJECT_ID}/call/{call_id}"

# Attach both feedback items.
for fb in (human_note, scorer_feedback):
    r = requests.post(
        f"{BASE_URL}/feedback/create",
        auth=AUTH,
        json={
            "project_id": PROJECT_ID,
            "weave_ref": call_ref,
            "feedback_type": fb["feedback_type"],
            "payload": fb["payload"],
        },
    )
    r.raise_for_status()
    print(f"Feedback: id={r.json()['id']} type={fb['feedback_type']}")

# --- verification ---
# Query feedback filtered to this Call by weave_ref, asserting both
# items land with the expected feedback_type + payload. Brief retry
# tolerates eventual consistency in the read path.
expected_types = {human_note["feedback_type"], scorer_feedback["feedback_type"]}
rows: list[dict] = []
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
                        {"$literal": call_ref},
                    ]
                }
            },
        },
    )
    r.raise_for_status()
    rows = r.json().get("result", [])
    if expected_types <= {row["feedback_type"] for row in rows}:
        break
    time.sleep(1)
else:
    sys.exit(f"FAIL: feedback for {call_ref} not all visible after 5 reads (got {[row['feedback_type'] for row in rows]})")

by_type = {row["feedback_type"]: row for row in rows}
assert by_type[human_note["feedback_type"]]["payload"] == human_note["payload"], (
    f"human payload: {by_type[human_note['feedback_type']]['payload']!r}"
)
assert by_type[scorer_feedback["feedback_type"]]["payload"] == scorer_feedback["payload"], (
    f"scorer payload: {by_type[scorer_feedback['feedback_type']]['payload']!r}"
)
for row in rows:
    assert row["weave_ref"] == call_ref, f"weave_ref drift: {row['weave_ref']!r}"
print(f"Verified: {len(by_type)} feedback items on {call_ref}")
