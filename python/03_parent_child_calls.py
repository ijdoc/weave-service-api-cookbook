# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 03: parent + child Calls (RAG-shaped trace).

Demonstrates Trace structure: one parent Call with two child Calls
underneath. Children declare their parent via `parent_id` on
/call/start and share the parent's `trace_id` explicitly.

The RAG-shaped flow:
    rag_pipeline (parent)
    ├── retrieve  (child 1)
    └── generate  (child 2)

Ordering matters: a child's /call/start happens after the parent's
/call/start, and each child's /call/end happens before the parent's
/call/end. The recipe shows this canonical order.

Verification queries /calls/stream_query by trace_id, gets all three
Calls back, and asserts the parent/child structure is what we wrote.

Run:
    uv run python/03_parent_child_calls.py
"""
import json
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
    "cookbook.recipe": "03_parent_child_calls",
    "cookbook.environment": os.environ.get("COOKBOOK_ENVIRONMENT", "dev"),
}


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def start_call(op_name: str, inputs: dict, *, parent_id: str | None = None, trace_id: str | None = None) -> dict:
    """POST /call/start. Returns the {id, trace_id} response dict."""
    payload = {
        "start": {
            "project_id": PROJECT_ID,
            "op_name": op_name,
            "started_at": now(),
            "attributes": base_attributes,
            "inputs": inputs,
        }
    }
    if parent_id is not None:
        payload["start"]["parent_id"] = parent_id
    if trace_id is not None:
        payload["start"]["trace_id"] = trace_id
    r = requests.post(f"{BASE_URL}/call/start", auth=AUTH, json=payload)
    r.raise_for_status()
    return r.json()


def end_call(call_id: str, output: dict) -> None:
    """POST /call/end."""
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


# Open the parent (top-level: no parent_id, no explicit trace_id).
# The server assigns a trace_id which we propagate to children.
parent = start_call("recipe-03-rag-pipeline", {"question": "Where is the Eiffel Tower?"})
parent_id = parent["id"]
trace_id = parent["trace_id"]
print(f"Started parent: id={parent_id} trace_id={trace_id}")

# Open + finish the first child (retrieve), passing the parent's id and trace_id.
retrieve = start_call(
    "recipe-03-retrieve",
    {"question": "Where is the Eiffel Tower?"},
    parent_id=parent_id,
    trace_id=trace_id,
)
retrieve_id = retrieve["id"]
print(f"Started child 1: id={retrieve_id}")
end_call(retrieve_id, {"docs": ["Paris", "France"]})
print(f"Ended   child 1: id={retrieve_id}")

# Open + finish the second child (generate).
generate = start_call(
    "recipe-03-generate",
    {"docs": ["Paris", "France"], "question": "Where is the Eiffel Tower?"},
    parent_id=parent_id,
    trace_id=trace_id,
)
generate_id = generate["id"]
print(f"Started child 2: id={generate_id}")
end_call(generate_id, {"answer": "In Paris, France."})
print(f"Ended   child 2: id={generate_id}")

# Close the parent (after all children have finished).
end_call(parent_id, {"answer": "In Paris, France."})
print(f"Ended   parent:  id={parent_id}")

# --- verification ---
# Stream all Calls in this trace; assert parent + 2 children, with
# parent.parent_id = None and children.parent_id = parent_id.
expected = {parent_id, retrieve_id, generate_id}
found_ids: dict[str, dict] = {}
for _ in range(5):
    with requests.post(
        f"{BASE_URL}/calls/stream_query",
        auth=AUTH,
        json={
            "project_id": PROJECT_ID,
            "filter": {"trace_ids": [trace_id]},
        },
        stream=True,
    ) as r:
        r.raise_for_status()
        rows = [json.loads(line) for line in r.iter_lines(decode_unicode=True) if line]
    found_ids = {c["id"]: c for c in rows}
    # Require all three calls visible AND finalized (ended_at populated)
    # so we don't race write-to-read propagation on inner-field reads.
    if expected <= found_ids.keys() and all(found_ids[i].get("ended_at") for i in expected):
        break
    time.sleep(1)

missing_ids = expected - found_ids.keys()
if missing_ids:
    sys.exit(f"FAIL: trace {trace_id} missing calls: {missing_ids}")

parent_call = found_ids[parent_id]
retrieve_call = found_ids[retrieve_id]
generate_call = found_ids[generate_id]

assert parent_call.get("parent_id") is None, f"parent has parent_id: {parent_call['parent_id']!r}"
assert retrieve_call["parent_id"] == parent_id, f"retrieve.parent_id: {retrieve_call['parent_id']!r}"
assert generate_call["parent_id"] == parent_id, f"generate.parent_id: {generate_call['parent_id']!r}"

for call in (parent_call, retrieve_call, generate_call):
    assert call["trace_id"] == trace_id, f"trace_id on {call['id']}: {call['trace_id']!r}"
    for key, value in base_attributes.items():
        assert call["attributes"].get(key) == value, (
            f"attribute {key} on {call['id']}: {call['attributes'].get(key)!r}"
        )

print(f"Verified: trace_id={trace_id} (1 parent + 2 children)")
