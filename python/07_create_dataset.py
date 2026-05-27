# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 07: create a Dataset and read its rows back.

Demonstrates the v2 Dataset endpoints plus the Table read needed to
walk the rows:
    POST   /v2/{entity}/{project}/datasets
        -> create the Dataset, returns (object_id, digest, version_index)
    GET    /v2/{entity}/{project}/datasets/{object_id}/versions/{digest}
        -> read Dataset metadata, including a *reference* to its rows
    POST   /table/query
        -> read the actual rows out of the referenced Table

Three wire-level points worth knowing:

- These are the v2 endpoints under `/v2/{entity}/{project}/datasets`,
  not a v1-style `POST /datasets/create`. Entity and project live in
  the URL path rather than in the request body. Read uses GET (the
  rest of the service API is POST-only); create uses POST with a JSON
  body.
- A Dataset is addressed by `(object_id, digest)`. `object_id` is
  stable across versions; `digest` pins a specific version. Datasets
  with the same `name` accumulate as new versions of one logical
  Dataset. Datasets are **content-addressed** — identical (name, rows)
  collapses to the same `(digest, version_index)`. To make sure the
  recipe actually exercises the write path on every run (rather than
  silently resolving to an existing object), the dataset name is
  stamped with a per-run Unix-epoch timestamp.
- The Dataset read response's `rows` field is a *reference string* to
  the underlying Table, not the row data. To walk rows, parse the
  table digest out of that reference and call `/table/query`. Rows are
  wrapped as `{digest, val, original_index?}` — the actual row content
  lives under `val`.

Run:
    uv run python/07_create_dataset.py
"""
import os
import re
import sys
import time
from datetime import datetime, timezone

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

dataset_name = f"recipe-07-dataset-{int(time.time())}"
dataset_description = f"Capital cities for evaluation (run at {datetime.now(timezone.utc).isoformat()})"
dataset_rows = [
    {"question": "What is the capital of France?", "answer": "Paris"},
    {"question": "What is the capital of Spain?", "answer": "Madrid"},
    {"question": "What is the capital of Italy?", "answer": "Rome"},
]

# Create the Dataset. v2 path; entity + project go into the URL.
r = requests.post(
    f"{BASE_URL}/v2/{ENTITY}/{PROJECT}/datasets",
    auth=AUTH,
    json={
        "name": dataset_name,
        "description": dataset_description,
        "rows": dataset_rows,
    },
)
r.raise_for_status()
created = r.json()
object_id = created["object_id"]
digest = created["digest"]
version_index = created["version_index"]
print(f"Created: object_id={object_id} digest={digest[:12]}… version={version_index}")

# Read Dataset metadata back. GET, with object_id + digest in the URL.
r = requests.get(
    f"{BASE_URL}/v2/{ENTITY}/{PROJECT}/datasets/{object_id}/versions/{digest}",
    auth=AUTH,
)
r.raise_for_status()
dataset = r.json()
assert dataset["name"] == dataset_name, f"name: {dataset['name']!r}"
assert dataset["description"] == dataset_description, f"description: {dataset['description']!r}"
assert dataset["object_id"] == object_id, f"object_id drift: {dataset['object_id']!r}"
assert dataset["digest"] == digest, f"digest drift: {dataset['digest']!r}"
print(f"Read:    name={dataset['name']!r} rows_ref={dataset['rows']!r}")

# The rows field is a reference to a Table. Parse out the table digest
# so we can /table/query it. The format observed in practice is a
# weave URI like `weave:///{entity}/{project}/table/{digest}`; tolerate
# the bare-digest form too in case the shape varies.
rows_ref = dataset["rows"]
m = re.search(r"/table/([A-Za-z0-9_-]+)$", rows_ref)
table_digest = m.group(1) if m else rows_ref
print(f"Table digest: {table_digest[:12]}…")

# Query the actual rows.
r = requests.post(
    f"{BASE_URL}/table/query",
    auth=AUTH,
    json={"project_id": PROJECT_ID, "digest": table_digest},
)
r.raise_for_status()
rows = r.json()["rows"]

# --- verification ---
# Row count + first-row content must match what we wrote.
assert len(rows) == len(dataset_rows), f"row count: {len(rows)} vs {len(dataset_rows)}"
# Row wrappers carry the row digest + the actual value under `val`.
for i, (row, expected) in enumerate(zip(rows, dataset_rows)):
    assert row["val"] == expected, f"row {i} val: {row['val']!r} vs {expected!r}"

print(f"Verified: {len(rows)} rows match (first: {rows[0]['val']!r})")
