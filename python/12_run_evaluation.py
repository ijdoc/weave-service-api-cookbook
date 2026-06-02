# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 12: run an evaluation as a 4-level Call trace.

The integration recipe. Looks up everything earlier recipes created,
builds the structured Call tree the W&B UI recognises as an evaluation
run, and verifies via /eval_results/query. **Lands ADR-0005** (the
imperative-SDK-path decision).

The trace shape is what the SDK's `evaluation.evaluate(model)` produces:

    Evaluation.evaluate                            (root, op_name = canonical)
    ├── Evaluation.predict_and_score              (per-row trial)
    │   ├── <Model>.predict                        (the model invocation)
    │   └── <scorer>                               (scoring)
    ├── Evaluation.predict_and_score              (row 2)
    │   ├── ...
    │   └── ...
    ├── Evaluation.predict_and_score              (row 3)
    │   ├── ...
    │   └── ...
    └── Evaluation.summarize                       (sibling of predict_and_score)

What this recipe owns vs what it looks up:

- *Looks up* (created by earlier recipes):
    - Evaluation Object         <- recipe 11 (extract refs from its val)
    - canonical Eval Ops        <- recipe 11 (`Evaluation.evaluate`, etc.)
    - Scorer Op                 <- recipe 11's eval val (`scorers[0]`)
    - Dataset                   <- recipe 11's eval val (`dataset`)
    - Model + its predict Op    <- recipe 08
- *Creates*: only Calls. No new Objects or Ops here — recipe 11 owns
  the eval's definition surface, recipe 12 just executes one run.

Wire-level points worth knowing:

- **Per-Call op_name MUST be a weave:// URI** to an existing Op, not a
  raw string. The W&B UI's `parseRef` crashes on raw strings.
- **The root Call's `display_name`** is what the Evaluations UI surfaces
  as the run's label. Without it, the page falls back to the op_name
  (`Evaluation.evaluate`) which makes every run look the same. This
  recipe sets `display_name = "eval-<language>-<unix-epoch>"`.
- **Root `/call/end` summary** needs `weave.status="success"` and
  `status_counts.success` = total number of calls in the trace (1 +
  N × 3 + 1 for N dataset rows). Without these, the UI marks the run
  as "in progress" or "failed".
- **Inputs use raw row values** for simplicity. The SDK uses deep
  weave:// refs into the Dataset's table rows so the UI can navigate
  back to the source dataset cell. Both work for /eval_results/query;
  the cookbook keeps raw values for readability.
- **The model invocation is mocked** — we pretend the model always
  returns the expected answer, so `pass_rate` is 1.0. A real recipe
  would call the LLM named in the Model's `model_name` attribute (see
  recipe 08) and use the actual response.

Run:
    uv run python/12_run_evaluation.py
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


def post(path: str, body: dict) -> dict:
    r = requests.post(f"{BASE_URL}{path}", auth=AUTH, json=body)
    r.raise_for_status()
    return r.json()


def get(path: str) -> dict:
    r = requests.get(f"{BASE_URL}{path}", auth=AUTH)
    r.raise_for_status()
    return r.json()


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def latest_object(object_id: str) -> dict | None:
    r = post("/objs/query", {
        "project_id": PROJECT_ID,
        "filter": {"object_ids": [object_id], "latest_only": True},
        "metadata_only": False,
    })
    objs = r.get("objs", [])
    return objs[0] if objs else None


def start_call(op_name: str, inputs: dict, *, parent_id: str | None = None,
               trace_id: str | None = None, display_name: str | None = None) -> tuple[str, str]:
    payload = {
        "project_id": PROJECT_ID,
        "op_name": op_name,
        "started_at": now(),
        "attributes": {
            "cookbook.language": "python",
            "cookbook.recipe": "12_run_evaluation",
            "cookbook.environment": os.environ.get("COOKBOOK_ENVIRONMENT", "dev"),
        },
        "inputs": inputs,
    }
    if parent_id is not None:
        payload["parent_id"] = parent_id
    if trace_id is not None:
        payload["trace_id"] = trace_id
    if display_name is not None:
        payload["display_name"] = display_name
    r = post("/call/start", {"start": payload})
    return r["id"], r["trace_id"]


def end_call(call_id: str, output, *, summary_extras: dict | None = None) -> None:
    summary = {
        "status_counts": {"success": 1, "error": 0},
        "weave": {"status": "success"},
    }
    if summary_extras:
        summary["status_counts"].update(summary_extras.get("status_counts", {}))
        summary["weave"].update(summary_extras.get("weave", {}))
    post("/call/end", {"end": {
        "project_id": PROJECT_ID,
        "id": call_id,
        "ended_at": now(),
        "summary": summary,
        "output": output,
    }})


# 1) Look up the Evaluation Object + extract refs from its val.
# Recipe 11's val carries the canonical Op refs + dataset + scorer.
eval_obj = latest_object("recipe-11-eval-python")
if eval_obj is None:
    sys.exit("FAIL: Evaluation Object `recipe-11-eval-python` not found. Run python/11_create_evaluation.py first.")
eval_obj_ref = f"weave:///{PROJECT_ID}/object/{eval_obj['object_id']}:{eval_obj['digest']}"
ev = eval_obj["val"]
evaluate_op_ref = ev["evaluate"]
predict_and_score_op_ref = ev["predict_and_score"]
summarize_op_ref = ev["summarize"]
scorer_op_ref = ev["scorers"][0]
dataset_ref = ev["dataset"]
# The scorer Op's short_name (object_id) is the key the leaderboard
# aggregator uses to bucket per-row scores. Compute once; reuse for
# the per-row `scores` dict, the wandb.runnable.* feedback_type, and
# the summarize + root output keys.
scorer_short_name = scorer_op_ref.rsplit("/op/", 1)[-1].split(":", 1)[0]
print(f"Eval obj:  {eval_obj['object_id']} digest={eval_obj['digest'][:12]}…")


# 2) Look up the Model + its predict Op (recipe 08).
model_obj = latest_object("recipe-08-model-python")
if model_obj is None:
    sys.exit("FAIL: Model `recipe-08-model-python` not found. Run python/08_use_model.py first.")
model_ref = f"weave:///{PROJECT_ID}/object/{model_obj['object_id']}:{model_obj['digest']}"

model_predict_op = latest_object("recipe-08-model-python.predict")
if model_predict_op is None:
    sys.exit("FAIL: Model predict Op `recipe-08-model-python.predict` not found. Run python/08_use_model.py first.")
model_predict_op_ref = f"weave:///{PROJECT_ID}/op/{model_predict_op['object_id']}:{model_predict_op['digest']}"
print(f"Model:     {model_obj['object_id']} digest={model_obj['digest'][:12]}…")


# 3) Walk the Dataset rows. dataset_ref is a weave:// URI; the v2 read
# returns a `rows` field that's another ref into a Table; /table/query
# yields the actual row data.
m = re.match(r"weave:///[^/]+/[^/]+/object/([^:]+):(.+)", dataset_ref)
if m is None:
    sys.exit(f"FAIL: could not parse dataset_ref: {dataset_ref!r}")
ds_id, ds_digest = m.group(1), m.group(2)
ds_meta = get(f"/v2/{ENTITY}/{PROJECT}/datasets/{ds_id}/versions/{ds_digest}")
rows_ref = ds_meta["rows"]
m = re.search(r"/table/([A-Za-z0-9_-]+)$", rows_ref)
table_digest = m.group(1) if m else rows_ref
rows_res = post("/table/query", {"project_id": PROJECT_ID, "digest": table_digest})
rows = [row["val"] for row in rows_res["rows"]]
print(f"Dataset:   {ds_id} ({len(rows)} rows)")


# 4) Build the 4-level Call trace. The display_name on the root is the
# Evaluations-page label; without it the page shows the bare op_name.
display_name = f"eval-python-{int(time.time())}"
root_id, trace_id = start_call(
    evaluate_op_ref,
    inputs={"self": eval_obj_ref, "model": model_ref},
    display_name=display_name,
)
print(f"Root call: {root_id} (display_name={display_name!r})")

n_pass = 0
total_calls = 1  # root
# Fixed per-row latency stub — recipe 12's "model" is a deterministic
# echo, so timing is meaningless. Both the per-row predict_and_score
# output and the aggregated summarize/root output include it because
# that's what the SDK emits and what the UI's aggregator expects to
# average across rows.
MODEL_LATENCY = 0.001
for i, row in enumerate(rows):
    ps_id, _ = start_call(
        predict_and_score_op_ref,
        inputs={"self": eval_obj_ref, "model": model_ref, "example": row},
        parent_id=root_id, trace_id=trace_id,
    )

    # Predict child: invoke the (mocked) model.
    pred_id, _ = start_call(
        model_predict_op_ref,
        inputs={"self": model_ref, "question": row["question"]},
        parent_id=ps_id, trace_id=trace_id,
    )
    # Mock: pretend the model always returns the expected answer.
    # A real recipe would call the LLM named in the Model's `model_name`
    # attribute (recipe 08) and use its response here.
    prediction = row["answer"]
    end_call(pred_id, output={"answer": prediction})

    # Scorer child: compare prediction vs expected.
    sc_id, _ = start_call(
        scorer_op_ref,
        inputs={"output": prediction, "expected": row["answer"]},
        parent_id=ps_id, trace_id=trace_id,
    )
    score = prediction == row["answer"]
    end_call(sc_id, output=score)

    # Link the score to the predict Call via a `wandb.runnable.*`
    # Feedback row — same pattern as recipe 09's apply_scorer. The
    # SDK adds this on every per-row predict during eval.evaluate();
    # without it, the score shows in the per-row output but there's
    # no scorer-Op attribution at the leaderboard level (cross-model
    # comparison views key off these Feedback rows). Recipe 12 has to
    # post them explicitly because we're driving the trace directly.
    pred_call_ref = f"weave:///{PROJECT_ID}/call/{pred_id}"
    score_call_ref = f"weave:///{PROJECT_ID}/call/{sc_id}"
    post("/feedback/create", {
        "project_id": PROJECT_ID,
        "weave_ref": pred_call_ref,
        "feedback_type": f"wandb.runnable.{scorer_short_name}",
        "payload": {"output": score},
        "runnable_ref": scorer_op_ref,
        "call_ref": score_call_ref,
    })

    # End predict_and_score with the per-row aggregated output. The
    # SDK includes a model_latency value here too.
    #
    # CRITICAL: the key in `scores` MUST be the scorer Op's short name
    # (its `object_id`) — same string used in the wandb.runnable.*
    # feedback_type above. This is what links the per-row scorer_key
    # in /eval_results/query's response back to the Eval Object's
    # val.scorers list, which is what powers the UI's scorer-object
    # attribution and the cross-model leaderboard view. The SDK uses
    # the scorer function's name, which happens to equal its object_id;
    # we have to derive ours from scorer_op_ref since our object_id
    # (`recipe-09-is-correct-<lang>`) differs from the scaffold's
    # function name (`is_correct`).
    end_call(
        ps_id,
        output={"output": prediction, "scores": {scorer_short_name: score}, "model_latency": MODEL_LATENCY},
    )

    if score:
        n_pass += 1
    total_calls += 3  # predict_and_score + predict + scorer

# Summarize: sibling of predict_and_score under the root. Carries the
# aggregated scorer stats.
sum_id, _ = start_call(
    summarize_op_ref,
    inputs={"self": eval_obj_ref},
    parent_id=root_id, trace_id=trace_id,
)
pass_rate = n_pass / len(rows) if rows else 0.0
# Both summarize.output AND root.output must be keyed by the scorer's
# short_name (matching val.scorers[i] and the per-row scorer_key) and
# carry a `model_latency.mean` field. This dict IS what the leaderboard
# view reads: it buckets values across runs by these top-level keys to
# render the cross-model comparison table. A key that doesn't match
# val.scorers — or a missing model_latency aggregate — and the row
# silently drops out of the leaderboard.
aggregated_output = {
    scorer_short_name: {"true_count": n_pass, "true_fraction": pass_rate},
    "model_latency": {"mean": MODEL_LATENCY},
}
end_call(sum_id, output=aggregated_output)
total_calls += 1  # summarize


# 5) End the root with the proper summary shape — status_counts.success
# is the total call count; weave.status="success" + display_name make
# the UI render the run as finished.
post("/call/end", {"end": {
    "project_id": PROJECT_ID,
    "id": root_id,
    "ended_at": now(),
    "summary": {
        "status_counts": {"success": total_calls, "error": 0},
        "weave": {"status": "success", "display_name": display_name},
    },
    "output": aggregated_output,
}})
print(f"Trace done: {total_calls} calls, pass_rate={pass_rate:.2f}")


# --- verification ---
# /eval_results/query with the root call_id aggregates per-row trial
# data + scorer stats. The summary's evaluation_ref should match the
# Eval Object we ran against.
time.sleep(2)
results = None
for _ in range(8):
    r = post(f"/v2/{ENTITY}/{PROJECT}/eval_results/query", {
        "evaluation_call_ids": [root_id],
        "include_rows": True,
        "include_summary": True,
    })
    if r.get("total_rows") == len(rows):
        results = r
        break
    time.sleep(1)
else:
    sys.exit(f"FAIL: eval_results/query did not return {len(rows)} rows after 8 attempts (last={r.get('total_rows')!r})")

evals = results["summary"]["evaluations"]
assert len(evals) == 1, f"expected 1 evaluation in summary, got {len(evals)}"
ev_summary = evals[0]
assert ev_summary["evaluation_ref"] == eval_obj_ref, f"evaluation_ref: {ev_summary['evaluation_ref']!r}"
scorer_keys = [s["scorer_key"] for s in ev_summary["scorer_stats"]]
expected_scorer_key = scorer_op_ref.rsplit("/op/", 1)[-1].split(":", 1)[0]
assert expected_scorer_key in scorer_keys, f"{expected_scorer_key!r} missing from scorer_stats: {scorer_keys!r}"
print(f"Verified:  /eval_results/query returned {results['total_rows']} rows, evaluation_ref matches, scorer_stats={scorer_keys}")
