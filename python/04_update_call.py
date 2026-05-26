# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 04: update a Call's display_name after it finishes.

Demonstrates the only mutation the service API exposes on a finished
Call:
    POST /call/update  -> change display_name

Two wire-level quirks worth noting:

- The body is **flat**: top-level `project_id`, `call_id`, `display_name`.
  /call/start and /call/end wrap their bodies under `start` / `end`;
  /call/update does not. Sending `{"update": {...}}` will 422.
- The id field is named `call_id`, not `id` (which is what /call/end
  uses).

The schema's other constraint is that `display_name` is the only
user-modifiable field. `attributes`, `inputs`, `output`, etc. are
immutable after /call/start.

Run:
    uv run python/04_update_call.py
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

op_name = "recipe-04-update-call"
attributes = {
    "cookbook.language": "python",
    "cookbook.recipe": "04_update_call",
    "cookbook.environment": os.environ.get("COOKBOOK_ENVIRONMENT", "dev"),
}
inputs = {"question": "What is the capital of Italy?"}
output = {"answer": "Rome"}
new_display_name = "recipe 04 — updated after finish"

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
trace_id = r.json()["trace_id"]
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

# Mutate display_name. Flat body, `call_id` (not `id`), no wrapper key.
r = requests.post(
    f"{BASE_URL}/call/update",
    auth=AUTH,
    json={
        "project_id": PROJECT_ID,
        "call_id": call_id,
        "display_name": new_display_name,
    },
)
r.raise_for_status()
print(f"Updated: id={call_id} display_name={new_display_name!r}")

# --- verification ---
# Read the Call back and assert display_name reflects the update.
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
    if call and call.get("display_name") == new_display_name:
        break
    time.sleep(1)
else:
    sys.exit(f"FAIL: Call {call_id} display_name not updated after 5 reads")

assert call["display_name"] == new_display_name, f"display_name: {call['display_name']!r}"
# op_name and the rest must NOT have changed — /call/update only touches display_name.
assert call["op_name"] == op_name, f"op_name drifted: {call['op_name']!r}"
for key, value in attributes.items():
    assert call["attributes"].get(key) == value, f"attribute {key}: {call['attributes'].get(key)!r}"
assert call["inputs"] == inputs, f"inputs: {call['inputs']!r}"
assert call["output"] == output, f"output: {call['output']!r}"
assert call["trace_id"] == trace_id, f"trace_id: {call['trace_id']!r}"
print(f"Verified: id={call_id} display_name={call['display_name']!r}")
