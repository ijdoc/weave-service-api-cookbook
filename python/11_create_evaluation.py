# /// script
# requires-python = ">=3.10"
# dependencies = ["requests>=2.31"]
# ///
"""Recipe 11: set up an Evaluation Object.

Pulls everything from earlier recipes together into a single Evaluation
**definition** — the versioned, content-addressed Object that recipe 12
will execute and recipe 13 will query against. After this recipe runs,
the W&B UI's *Evaluation Definitions* page (`/weave/evaluation-definitions`)
shows it as a browsable definition with no associated runs yet.

The recipe builds two kinds of artifacts:

1. **Three canonical Eval Ops** (`Evaluation.evaluate`,
   `Evaluation.predict_and_score`, `Evaluation.summarize`) — inert
   lifecycle-marker Ops registered via a two-step
   `/file/create` + `/obj/create` flow with ADR-0004 scaffolds.
   The W&B service identifies these Ops by their `object_id` and
   uses them to recognise an evaluation Call trace
   (`/eval_results/query` filters on the exact canonical names,
   case-sensitive). The source is a stub `raise NotImplementedError`;
   the real eval logic lives in recipe 12 client-side.
   Content-addressed — re-running an unchanged recipe 11 is a no-op;
   editing this recipe bumps the Op versions (and downstream the
   Eval Object version too).

2. **The Evaluation Object itself** — built via `POST /obj/create`
   with `builtin_object_class="Evaluation"`, referencing the freshly
   registered canonical Ops + the recipe-08 Model + the recipe-09
   Scorer Op + the recipe-10 Dataset, all by weave:// URI.

Recipe 12 (Run an evaluation) will look up the canonical Eval Ops and
the Eval Object created here; recipe 13 (Query results) does the same.
**Don't duplicate the scaffolds in recipes 12 / 13** — they live here
only, so editing the eval's definition is a single-file change and
the Eval Object version bumps atomically with the scaffold edits.

Wire-level points worth knowing:

- **`/obj/create` with `builtin_object_class="Evaluation"`** is the
  cookbook's chosen path (matching the SDK). The specialized
  `POST /v2/.../evaluations` endpoint also exists but auto-creates
  per-eval-aliased Ops (`<eval-id>.evaluate`) that the cookbook
  doesn't use — `/eval_results/query` filters by canonical name, not
  per-eval-aliased name. ADR-0005 (lands with recipe 12) captures
  this decision in detail.
- **Canonical Eval Op reuse**: once registered, the three Ops are
  shared across every Evaluation Object in the project. They're not
  per-eval. The SDK reuses them too — if any SDK eval has run in the
  project, content-addressing collapses identical scaffolds into the
  same digest. Edits to *this* recipe's scaffold create a new
  version, and any newly-built Eval Object points to the new digest.
- **Lookups, not inline creation, for recipe-08/09/10 outputs.** If
  any prerequisite is missing the recipe aborts with `Run python/0X_*.py
  first`. Recipe 11 owns the canonical Eval Op scaffolds because they
  are conceptually part of the eval's definition surface; everything
  else has its own recipe.

Run:
    uv run python/11_create_evaluation.py
"""
import hashlib
import os
import sys
import time
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

RECIPE_PATH = "python/11_create_evaluation.py"
RECIPE_SHA = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()[:16]


def post(path: str, body: dict) -> dict:
    r = requests.post(f"{BASE_URL}{path}", auth=AUTH, json=body)
    r.raise_for_status()
    return r.json()


def put(path: str, body: dict) -> dict:
    r = requests.put(f"{BASE_URL}{path}", auth=AUTH, json=body)
    r.raise_for_status()
    return r.json()


def upload_op_source(source: str) -> str:
    """Upload Op source as a file (multipart) and return the file digest.

    /file/create is the ONE multipart endpoint the cookbook uses; every
    other endpoint takes JSON. The returned digest goes into the Op's
    val under `files.obj.py`.
    """
    r = requests.post(
        f"{BASE_URL}/file/create",
        auth=AUTH,
        files={"file": ("obj.py", source.encode("utf-8"), "application/octet-stream")},
        data={"project_id": PROJECT_ID},
    )
    r.raise_for_status()
    return r.json()["digest"]


def latest_object(object_id: str) -> dict | None:
    """Return the latest version of `object_id`, or None if not present."""
    r = post("/objs/query", {
        "project_id": PROJECT_ID,
        "filter": {"object_ids": [object_id], "latest_only": True},
        "metadata_only": True,
    })
    objs = r.get("objs", [])
    return objs[0] if objs else None


def latest_dataset_by_prefix(prefix: str) -> dict | None:
    """Find the most-recently-created Dataset whose object_id starts with `prefix`.

    Recipe 10 timestamps Dataset names so exact object_id lookup won't
    work — list Datasets sorted desc by created_at and pick the first
    prefix match.
    """
    r = post("/objs/query", {
        "project_id": PROJECT_ID,
        "filter": {"base_object_classes": ["Dataset"]},
        "sort_by": [{"field": "created_at", "direction": "desc"}],
        "limit": 50,
        "metadata_only": True,
    })
    for o in r.get("objs", []):
        if o["object_id"].startswith(prefix):
            return o
    return None


# 1) Look up the prerequisites from earlier recipes. Abort with a clear
# pointer to the recipe that would create the missing artifact.
model = latest_object("recipe-08-model-python")
if model is None:
    sys.exit("FAIL: model `recipe-08-model-python` not found. Run python/08_use_model.py first.")
print(f"Found:     model    {model['object_id']} digest={model['digest'][:12]}…")

scorer = latest_object("recipe-09-is-correct-python")
if scorer is None:
    sys.exit("FAIL: scorer `recipe-09-is-correct-python` not found. Run python/09_score_a_call.py first.")
print(f"Found:     scorer   {scorer['object_id']} digest={scorer['digest'][:12]}…")

dataset = latest_dataset_by_prefix("recipe-10-dataset-python")
if dataset is None:
    sys.exit("FAIL: no Dataset matching `recipe-10-dataset-python-*` found. Run python/10_create_dataset.py first.")
print(f"Found:     dataset  {dataset['object_id']} digest={dataset['digest'][:12]}…")


# 2) Register the three canonical Eval Ops with ADR-0004 scaffolds.
# Content-addressed: re-running an unchanged recipe is a no-op (same
# digest stays); editing this recipe bumps version_index and (in
# step 3) bumps the Eval Object too.
def scaffold(op_name: str, signature: str, body_doc: str) -> str:
    """ADR-0004 scaffold for a canonical Eval Op. The Op's source is
    inert; the W&B service identifies it by `object_id`, not by what
    the body does."""
    return f"""# Cookbook scaffold (python)
# Source: {RECIPE_PATH}
# SHA256: {RECIPE_SHA}

import weave


@weave.op
def {signature}:
    \"\"\"{body_doc}

    Byte-for-byte reference (SHA256 of the recipe file):
        {RECIPE_SHA}

    To verify a local copy of the file matches (POSIX shell):
        shasum -a 256 {RECIPE_PATH} | cut -c1-16

    Canonical lifecycle-marker Op for the cookbook's eval flow. The
    W&B service identifies this Op by `object_id` ({op_name!r}) and
    uses it to recognise the structured Call trace recipe 12 builds.
    The body raises NotImplementedError by design — real eval logic
    lives client-side in recipe 12.
    \"\"\"
    raise NotImplementedError(
        "This op is a Python scaffold uploaded from a non-Python recipe. "
        "See the docstring above for the real source-language file and a "
        "verifiable byte-for-byte reference (SHA256)."
    )
"""


CANONICAL_OPS = {
    "Evaluation.evaluate": scaffold(
        "Evaluation.evaluate",
        "evaluate(self, model)",
        "Root of an evaluation Call trace. Wraps one full pass over\n        the dataset with the given model + scorers.",
    ),
    "Evaluation.predict_and_score": scaffold(
        "Evaluation.predict_and_score",
        "predict_and_score(self, example)",
        "Per-row child of the eval root. One trial = one dataset row\n        scored by all configured scorers.",
    ),
    "Evaluation.summarize": scaffold(
        "Evaluation.summarize",
        "summarize(self, eval_table)",
        "Final sibling of predict_and_score children under the root.\n        Aggregates per-row scorer outputs into evaluation-level stats.",
    ),
}

eval_op_refs = {}
for op_id, source in CANONICAL_OPS.items():
    file_digest = upload_op_source(source)
    res = post("/obj/create", {
        "obj": {
            "project_id": PROJECT_ID,
            "object_id": op_id,
            "val": {
                "_type": "CustomWeaveType",
                "files": {"obj.py": file_digest},
                "weave_type": {"type": "Op"},
            },
        },
    })
    eval_op_refs[op_id] = f"weave:///{PROJECT_ID}/op/{res['object_id']}:{res['digest']}"
    print(f"Op:        {res['object_id']} digest={res['digest'][:12]}… (file={file_digest[:12]}…)")


# 3) Build the Evaluation Object. The val mirrors the SDK shape: each
# canonical Op is a structured `method` field on the object (so the
# W&B UI can render them inline on the Eval Definitions page), and
# `scorers` is a list of Op refs.
def obj_ref(o: dict) -> str:
    return f"weave:///{PROJECT_ID}/object/{o['object_id']}:{o['digest']}"


def op_ref(o: dict) -> str:
    return f"weave:///{PROJECT_ID}/op/{o['object_id']}:{o['digest']}"


eval_object_id = "recipe-11-eval-python"
eval_val = {
    "_bases": ["Object", "BaseModel"],
    "_class_name": "Evaluation",
    "_type": "Evaluation",
    "name": eval_object_id,
    "description": "Cookbook evaluation definition (python recipe 11)",
    "dataset": obj_ref(dataset),
    "evaluate": eval_op_refs["Evaluation.evaluate"],
    "predict_and_score": eval_op_refs["Evaluation.predict_and_score"],
    "summarize": eval_op_refs["Evaluation.summarize"],
    "scorers": [op_ref(scorer)],
    "trials": 1,
    "evaluation_name": None,
    "metadata": None,
    "preprocess_model_input": None,
}
created = post("/obj/create", {
    "obj": {
        "project_id": PROJECT_ID,
        "object_id": eval_object_id,
        "val": eval_val,
        "builtin_object_class": "Evaluation",
    },
})
eval_digest = created["digest"]
eval_ref = f"weave:///{PROJECT_ID}/object/{eval_object_id}:{eval_digest}"
print(f"Published: {eval_object_id} digest={eval_digest[:12]}…")
print(f"  ref: {eval_ref}")


# 4) Tag + alias (recipe 07's pattern). Tags are per-version, additive,
# UI-visible labels; aliases are per-object_id named pointers.
env_tag = os.environ.get("COOKBOOK_ENVIRONMENT", "dev")
tags_to_add = [env_tag, "python"]
put(f"/objs/{eval_object_id}/versions/{eval_digest}/tags", {
    "project_id": PROJECT_ID,
    "tags": tags_to_add,
})
print(f"Tagged:    {tags_to_add} -> version {eval_digest[:12]}…")

aliases_to_set = ["staging"]
put(f"/objs/{eval_object_id}/aliases", {
    "project_id": PROJECT_ID,
    "digest": eval_digest,
    "aliases": aliases_to_set,
})
print(f"Aliased:   {aliases_to_set} -> version {eval_digest[:12]}…")


# --- verification ---
# Read the Eval Object back (with tags + aliases) and assert every ref
# + metadata field round-trips. Brief retry for read-after-write lag.
read_back = None
for _ in range(5):
    r = post("/obj/read", {
        "project_id": PROJECT_ID,
        "object_id": eval_object_id,
        "digest": eval_digest,
        "include_tags_and_aliases": True,
    })
    read_back = r.get("obj")
    if read_back:
        break
    time.sleep(1)
else:
    sys.exit(f"FAIL: Eval Object {eval_object_id}:{eval_digest} not visible after 5 reads")

val = read_back["val"]
assert val["_class_name"] == "Evaluation", f"_class_name: {val['_class_name']!r}"
assert val["dataset"] == obj_ref(dataset), f"dataset: {val['dataset']!r}"
assert val["evaluate"] == eval_op_refs["Evaluation.evaluate"], f"evaluate: {val['evaluate']!r}"
assert val["predict_and_score"] == eval_op_refs["Evaluation.predict_and_score"], f"predict_and_score: {val['predict_and_score']!r}"
assert val["summarize"] == eval_op_refs["Evaluation.summarize"], f"summarize: {val['summarize']!r}"
assert val["scorers"] == [op_ref(scorer)], f"scorers: {val['scorers']!r}"
assert val["trials"] == 1, f"trials: {val['trials']!r}"
assert read_back["base_object_class"] == "Evaluation", f"base_object_class: {read_back['base_object_class']!r}"
tags = read_back.get("tags") or []
aliases = read_back.get("aliases") or []
for t in tags_to_add:
    assert t in tags, f"tag {t!r} missing from {tags!r}"
for a in aliases_to_set:
    assert a in aliases, f"alias {a!r} missing from {aliases!r}"
print(f"Verified:  Eval Object refs + tags + aliases round-trip (tags={tags}, aliases={aliases})")
