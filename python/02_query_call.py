# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 02: query Calls via /calls/stream_query.

Demonstrates the workhorse read endpoint:
    POST /calls/stream_query  -> stream NDJSON of matching Calls

Sets up by creating one Call (op_name="recipe-02-query-call"), then
queries that op_name and confirms the just-created Call appears in
the streamed results.

The endpoint returns one JSON object per line (application/jsonl); we
parse line-by-line via requests' iter_lines rather than buffering.

Run:
    uv run python/02_query_call.py
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

op_name = "recipe-02-query-call"
attributes = {
    "cookbook.language": "python",
    "cookbook.recipe": "02_query_call",
    "cookbook.environment": os.environ.get("COOKBOOK_ENVIRONMENT", "dev"),
}
inputs = {"question": "What is the capital of Spain?"}
output = {"answer": "Madrid"}

# Setup: create + end a Call we can later query for.
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
print(f"Created: id={call_id}")

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

# Query: stream Calls matching our op_name, newest first. The endpoint
# returns NDJSON, so we iterate lines as they arrive rather than
# collecting the full response. Retry briefly to tolerate eventual
# consistency on the read path.
found = None
for _ in range(5):
    with requests.post(
        f"{BASE_URL}/calls/stream_query",
        auth=AUTH,
        json={
            "project_id": PROJECT_ID,
            "filter": {"op_names": [op_name]},
            "sort_by": [{"field": "started_at", "direction": "desc"}],
            "limit": 50,
        },
        stream=True,
    ) as r:
        r.raise_for_status()
        for line in r.iter_lines(decode_unicode=True):
            if not line:
                continue
            call = json.loads(line)
            if call["id"] == call_id:
                found = call
                break
    if found is not None:
        break
    time.sleep(1)

# --- verification ---
if found is None:
    sys.exit(f"FAIL: Call {call_id} not in stream_query results after 5 attempts")

assert found["op_name"] == op_name, f"op_name: {found['op_name']!r}"
for key, value in attributes.items():
    assert found["attributes"].get(key) == value, f"attribute {key}: {found['attributes'].get(key)!r}"
assert found["inputs"] == inputs, f"inputs: {found['inputs']!r}"
assert found["output"] == output, f"output: {found['output']!r}"
assert found["trace_id"] == trace_id, f"trace_id: {found['trace_id']!r}"
print(f"Verified: id={call_id}")
