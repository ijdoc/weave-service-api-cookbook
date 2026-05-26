# Weave service API

This glossary defines the domain language used throughout this repo. Terms mirror W&B's own usage in the [service API reference](https://docs.wandb.ai/weave/reference/service-api), with disambiguation where the SDK and the service have different vocabulary.

## Language

**Service API**:
The HTTP interface exposed by `https://trace.wandb.ai/`. Distinct from the Python `weave` SDK, which is one client layered over this surface. Endpoints documented at [docs.wandb.ai/weave/reference/service-api](https://docs.wandb.ai/weave/reference/service-api).
_Avoid_: REST API (synonymous but not the term W&B uses), Weave API (ambiguous between SDK and service).

**Call**:
A single traced operation, identified by an id returned from `/call/start`. Has inputs, outputs, status, and an optional `parent_id`. Plural form (`/calls/...`) appears in query and bulk endpoints.
_Avoid_: span (the OpenTelemetry term — not used by the service API), trace (means the tree, not a single node).

**Trace**:
The tree of Calls rooted at a top-level Call (one without a `parent_id`). Built implicitly by chaining `parent_id` across `/call/start` requests.
_Avoid_: span tree, call tree.

**Feedback**:
Annotation attached to a Call (or other object). Produced by humans (reviewer notes) or by scorers (during evaluation). Created via `/feedback/create`. Multiple feedback items can attach to one Call.

**Scorer feedback**:
Feedback produced by an automated scoring function, typically during an Evaluation Run. Conventionally tagged in the feedback payload to distinguish from human feedback.

**Dataset**:
A persisted collection of input rows used as the input set for an Evaluation. Created via `/datasets/create`.

**Evaluation object** ("Evaluation"):
The definition of an evaluation — which dataset to run against, which scorers to apply, what metadata to attach. Created via `/evaluations/create`. Distinct from the Evaluation Run that executes it.
_Avoid_: bare "evaluation" when the distinction with Evaluation Run matters.

**Evaluation Run**:
A specific execution of an Evaluation Object against a Model or callable. Has a lifecycle: created (`/evaluation-runs/create`), populated with predictions and scorer feedback, then finished (`/evaluation-runs/finish`).

**Eval Result**:
The output rows from an Evaluation Run — per-row predictions joined with scorer feedback. Queried via `/eval-results/query`.

## Why these terms matter

The Python SDK uses different vocabulary in places — for example, `weave.Evaluation` conflates the object and the run lifecycle. The service API is explicit: object first, then run. Using the service API directly means using the service API's vocabulary.
