# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 13: query evaluation results.

The "look at what already ran" recipe. Recipe 12 builds an evaluation
run; recipe 13 aggregates across runs and walks the per-trial data —
exactly what the W&B UI's *Evaluations* leaderboard view does.

Two endpoint patterns combined:

1. **`/calls/stream_query`** with `filter.op_names = [val.evaluate]` and
   `filter.trace_roots_only = true` — finds every root Call using the
   canonical `Evaluation.evaluate` Op. Returns NDJSON: one Call object
   per line.
2. **`/v2/{entity}/{project}/eval_results/query`** with
   `evaluation_call_ids = [<list of root call ids>]` — server-side
   aggregator that pulls each run's predict_and_score / scorer
   children, computes per-scorer stats per run, and (with
   `include_rows=true`) returns a row-major view of trial data so you
   can compare the same dataset row across runs.

What this recipe owns vs what it looks up:

- *Looks up* (created by earlier recipes):
    - Evaluation Object        -> recipe 11 (extract `val.evaluate` for
                                  the op_names filter)
    - One or more eval runs    -> recipe 12
- *Creates*: nothing. Pure read-only.

Wire-level points worth knowing:

- *Filter by op_names with a full weave:// ref*, not just the short
  name. `op_names = [evaluate_op_ref]` returns all root Calls bound
  to that exact Op version. Because the canonical Eval Ops are
  content-addressed and stable across runs, this is enough to find
  every run that used this eval definition's evaluate Op.
- *Filter by Eval Object client-side*. The canonical
  `Evaluation.evaluate` Op is *shared across Eval Objects of the
  same shape*; `op_names` alone returns runs across multiple Eval
  Objects. Narrow with `inputs.self.startswith(eval_obj_prefix)` —
  the prefix matches any version of our Eval Object's `object_id`.
- *`summary.evaluations[]` is one entry per *run*, not per Eval
  Object version. Each carries `evaluation_call_id`, `evaluation_ref`,
  `model_ref`, `display_name`, `started_at`, `trial_count`, and a
  `scorer_stats[]` array with rich aggregates (`pass_rate`,
  `pass_true_count`, `numeric_mean`, ...).
- *`rows[]` is row-major*. Each entry is keyed by the dataset row's
  content hash (`row_digest`), with a nested `evaluations[]` array
  whose `trials[]` give per-run, per-trial output + scores. So the
  same dataset row across multiple runs lives in one `rows[]` entry —
  that's what powers per-row cross-run comparison in the UI.

Run:
    uv run python/13_query_evaluation_results.py
"""
import json
import os
import sys

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

EVAL_OBJECT_ID = "recipe-11-eval-python"


def post(path: str, body: dict) -> dict:
    r = requests.post(f"{BASE_URL}{path}", auth=AUTH, json=body)
    r.raise_for_status()
    return r.json()


def post_ndjson(path: str, body: dict) -> list:
    # /calls/stream_query streams one JSON object per line, not a single
    # JSON document. Without parsing line-by-line, json.loads on the
    # raw body raises "Extra data" past the first line.
    r = requests.post(f"{BASE_URL}{path}", auth=AUTH, json=body)
    r.raise_for_status()
    return [json.loads(line) for line in r.text.splitlines() if line.strip()]


def latest_object(object_id: str) -> dict | None:
    body = post("/objs/query", {
        "project_id": PROJECT_ID,
        "filter": {"object_ids": [object_id], "latest_only": True},
        "metadata_only": False,
    })
    objs = body.get("objs", [])
    return objs[0] if objs else None


# 1) Look up the Eval Object (recipe 11). We need `val.evaluate` — the
# canonical Op ref — to scope the run search.
eval_obj = latest_object(EVAL_OBJECT_ID)
if eval_obj is None:
    sys.exit(f"FAIL: Evaluation Object `{EVAL_OBJECT_ID}` not found. Run python/11_create_evaluation.py first.")
evaluate_op_ref = eval_obj["val"]["evaluate"]
eval_obj_prefix = f"weave:///{PROJECT_ID}/object/{EVAL_OBJECT_ID}:"
print(f"Eval obj:   {EVAL_OBJECT_ID} (latest digest={eval_obj['digest'][:12]}…)")
print(f"Op filter:  {evaluate_op_ref}")


# 2) Find every root Call using this Evaluation.evaluate Op, then
# narrow to runs against our Eval Object (any version) by matching
# `inputs.self` against the object_id prefix.
roots = post_ndjson("/calls/stream_query", {
    "project_id": PROJECT_ID,
    "filter": {"trace_roots_only": True, "op_names": [evaluate_op_ref]},
    "limit": 50,
    "sort_by": [{"field": "started_at", "direction": "desc"}],
})
runs = [c for c in roots if (c.get("inputs") or {}).get("self", "").startswith(eval_obj_prefix)]
if not runs:
    sys.exit(f"FAIL: no eval runs against `{EVAL_OBJECT_ID}` found. Run python/12_run_evaluation.py first.")
print(f"Found:      {len(runs)} run(s) against `{EVAL_OBJECT_ID}` (any version)")


# 3) Aggregate across all of them via /eval_results/query. The server
# pulls each run's predict_and_score + scorer children, computes
# per-scorer stats per run, and (with include_rows) returns a
# row-major trial view.
res = post(f"/v2/{ENTITY}/{PROJECT}/eval_results/query", {
    "evaluation_call_ids": [c["id"] for c in runs],
    "include_rows": True,
    "include_summary": True,
})
total_rows = res["total_rows"]
evaluations = res["summary"]["evaluations"]
print(f"Aggregated: total_rows={total_rows}, evaluations in summary={len(evaluations)}\n")


# 4) Print the per-run leaderboard view: one line per run with the
# scorer aggregates the UI's Evaluations page shows.
print("RUNS (newest first):")
print(f"  {'display_name':<32}  {'started_at':<20}  {'trials':>6}  scorer summary")
for ev in evaluations:
    scorer_summary = ", ".join(
        f"{s['scorer_key']}={s['pass_true_count']}/{s['pass_known_count']} (pass_rate={s['pass_rate']:.2f})"
        for s in ev.get("scorer_stats", [])
    )
    started = ev.get("started_at", "")[:19]
    print(f"  {ev.get('display_name', '?')!s:<32}  {started:<20}  {ev['trial_count']:>6}  {scorer_summary}")


# 5) Per-row drill-down: walk the first row's evaluations to show how
# the same dataset row was answered across runs. This is what the UI's
# "compare across runs" view consumes.
print("\nROW 0 across all runs:")
row0 = res["rows"][0]
print(f"  row_digest={row0['row_digest'][:16]}…")
for run_block in row0["evaluations"]:
    call_id = run_block["evaluation_call_id"]
    run_label = next((ev.get("display_name", "?") for ev in evaluations if ev["evaluation_call_id"] == call_id), "?")
    for trial in run_block["trials"]:
        scores_str = ", ".join(f"{k}={v}" for k, v in (trial.get("scores") or {}).items())
        print(f"  - run={run_label!s:<32} output={trial['model_output']!r:<10} scores={{{scores_str}}}")


# --- verification ---
# All three load-bearing fields populated:
# - at least one run
# - per-run scorer_stats with the expected scorer key
# - per-row trial data
assert total_rows > 0, f"expected total_rows > 0, got {total_rows}"
assert len(evaluations) > 0, "no evaluations in summary"
scorer_keys_seen = {s["scorer_key"] for ev in evaluations for s in ev.get("scorer_stats", [])}
expected_scorer_key = eval_obj["val"]["scorers"][0].rsplit("/op/", 1)[-1].split(":", 1)[0]
assert expected_scorer_key in scorer_keys_seen, (
    f"scorer key {expected_scorer_key!r} missing from {sorted(scorer_keys_seen)!r} — "
    "did recipe 12 use the canonical scorer-Op object_id as the scores-dict key?"
)
assert res.get("rows"), "expected rows[] populated (include_rows=true)"
assert row0.get("evaluations"), "row 0 has no nested evaluations"
print(f"\nVerified:   {total_rows} trials across {len(evaluations)} run(s); scorer_keys={sorted(scorer_keys_seen)}")
