# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 01: start and finish a single Call.

Demonstrates the minimum Call lifecycle:
    POST /call/start  -> open the Call, capture id + trace_id
    POST /call/end    -> close it

Then verifies via POST /call/read that the Call landed and is finished.

Run:
    uv run python/01_start_call.py
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

op_name = "recipe-01-start-call"
attributes = {"cookbook.language": "python", "cookbook.recipe": "01_start_call"}
inputs = {"question": "What is the capital of France?"}
output = {"answer": "Paris"}

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
started = r.json()
call_id, trace_id = started["id"], started["trace_id"]
print(f"Started: id={call_id} trace_id={trace_id}")

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

# --- verification ---
# Read the Call back and assert the wire-state matches what we sent.
# Brief retry loop tolerates eventual consistency in the read path.
call = None
for _ in range(5):
    r = requests.post(
        f"{BASE_URL}/call/read",
        auth=AUTH,
        json={"project_id": PROJECT_ID, "id": call_id},
    )
    r.raise_for_status()
    call = r.json().get("call")
    if call and call.get("ended_at"):
        break
    time.sleep(1)
else:
    sys.exit(f"FAIL: Call {call_id} not visible/finished after 5 reads")

assert call["op_name"] == op_name, f"op_name: {call['op_name']!r}"
for key, value in attributes.items():
    assert call["attributes"].get(key) == value, f"attribute {key}: {call['attributes'].get(key)!r}"
assert call["inputs"] == inputs, f"inputs: {call['inputs']!r}"
assert call["output"] == output, f"output: {call['output']!r}"
assert call["trace_id"] == trace_id, f"trace_id: {call['trace_id']!r}"
print(f"Verified: id={call_id}")
