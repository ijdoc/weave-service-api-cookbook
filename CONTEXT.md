# Weave service API

This glossary defines the domain language used throughout this repo. Terms mirror W&B's own usage in the [service API reference](https://docs.wandb.ai/weave/reference/service-api), with disambiguation where the SDK and the service have different vocabulary.

## Language

**Service API**:
The HTTP interface exposed by `https://trace.wandb.ai/`. Distinct from the Python `weave` SDK, which is one client layered over this surface. Endpoints documented at [docs.wandb.ai/weave/reference/service-api](https://docs.wandb.ai/weave/reference/service-api).
_Avoid_: REST API (synonymous but not the term W&B uses), Weave API (ambiguous between SDK and service).

**Weave Object**:
A versioned, content-addressed entity stored on the Weave service. Created via `POST /obj/create` (generic) or one of the v2 specialized endpoints (`/datasets`, `/evaluations`, `/models`, `/scorers`, `/ops`). Identified by `(object_id, digest)` — `object_id` is stable across versions, `digest` pins a specific version. The `val` payload carries `_class_name`, `_bases`, `_type`, plus class-specific fields. Common base classes: Prompt, Model, Dataset, Evaluation, Scorer, Op.
_Avoid_: "Weave artifact" — artifacts are a separate W&B concept (binary blobs in W&B Core, not versioned objects in Weave).

**Op**:
A Weave Object representing a versioned callable identity (a function or method). Has a `source_code` field that the server stores verbatim and content-addresses. Created via `POST /v2/{entity}/{project}/ops`, or implicitly when other endpoints create dependent Ops (e.g., `POST /v2/.../evaluations` auto-creates `Evaluation.evaluate` / `.predict_and_score` / `.summarize` Ops). Referenced from a Call's `op_name` field as a `weave:///{entity}/{project}/op/{object_id}:{digest}` URI — never as a bare string in cookbook traces.
_Avoid_: conflating Op with Call. The Op is the versioned identity; the Call is one invocation of it.

**Call**:
A single traced operation, identified by an id returned from `/call/start`. Has inputs, outputs, status, and an optional `parent_id`. After `/call/end`, the Call's attributes, inputs, and output are immutable; only its display name can change (via `/call/update`). Plural form (`/calls/...`) appears in query and bulk endpoints.
_Avoid_: span (the OpenTelemetry term — not used by the service API), trace (means the tree, not a single node).

**Display name**:
The user-facing label for a Call in the Weave UI. Distinct from `op_name`, which identifies the *operation* a Call represents and serves as the default label when no display name is set. Settable at `/call/start` and changeable later via `/call/update`.
_Avoid_: conflating with `op_name` — display name is a presentation concern; `op_name` is identity.

**Trace**:
The tree of Calls rooted at a top-level Call (one without a `parent_id`). Built implicitly by chaining `parent_id` across `/call/start` requests.
_Avoid_: span tree, call tree.

**Feedback**:
Annotation attached to a Call (or other object). Produced by humans (reviewer notes) or by scorers (during evaluation). Created via `/feedback/create`. Multiple feedback items can attach to one Call.

**Scorer feedback**:
Feedback produced by an automated scoring function, typically during an Evaluation Run. Conventionally tagged in the feedback payload to distinguish from human feedback.
_Avoid_: confusing with a Scorer Op. Scorer feedback is the persisted Feedback row pattern (recipe 06); a Scorer Op (recipe 09) produces score values as Call outputs without necessarily writing a Feedback row.

**Prompt**:
A Weave Object representing a prompt template. `base_object_class="Prompt"`; subclasses include `StringPrompt` (raw text with `{var}` placeholders) and `MessagesPrompt` (list of `{role, content}` dicts, OpenAI-shaped). Created via `POST /obj/create` with `builtin_object_class="Prompt"`. Referenced from other objects (e.g., a Model's prompt attribute) or from a Call's inputs via a weave:// URI.

**Model**:
A Weave Object representing a versioned ML-model identity plus config. `base_object_class="Model"`. Created via `POST /v2/{entity}/{project}/models` (specialized) or `POST /obj/create` (generic). Pairs with a separate Op (typically named `<ModelName>.predict`) that captures the predict logic; a Call records the model's invocation by setting `op_name=<predict Op ref>` and `inputs.self=<Model ref>`.

**Scorer Op**:
An Op whose role is to score a model's output. The cookbook uses this Op-based pattern (matching the imperative SDK's `@weave.op def is_correct(...)`) rather than the formal Scorer Object endpoint (`POST /v2/.../scorers`) — keeps the cookbook's eval flow uniform with the rest of the Op pattern. Scorer Ops produce score values as Call outputs (binary/continuous/text).
_Avoid_: confusing with Scorer feedback (see above).

**Dataset**:
A persisted collection of input rows used as the input set for an Evaluation. `base_object_class="Dataset"`. Created via `POST /v2/{entity}/{project}/datasets`. Rows live in a separate Table object — the Dataset's `rows` field is a `weave://` reference, and walking the actual row data requires a follow-up `POST /table/query`. Content-addressed: identical `(name, rows)` collapses to the same `(digest, version_index)`.

**Evaluation object** ("Evaluation"):
The definition of an evaluation — which dataset to run against, which scorers and canonical Eval Ops to use, what metadata to attach. `base_object_class="Evaluation"`. The cookbook creates it via `POST /obj/create` with `builtin_object_class="Evaluation"` (the same path the SDK uses), referencing the Dataset, Model, Scorer Ops, and canonical `Evaluation.evaluate` / `.predict_and_score` / `.summarize` Ops via weave:// URIs. A specialized `POST /v2/{entity}/{project}/evaluations` endpoint also exists but auto-creates per-eval-aliased Ops the cookbook doesn't use. **UI: shown on the *Evaluation Definitions* page (`/weave/evaluation-definitions`), distinct from the *Evaluations* page (`/weave/evaluations`) which lists Evaluation Runs.**
_Avoid_: bare "evaluation" when the distinction with Evaluation Run matters.

**Evaluation Run**:
A specific execution of an Evaluation, materialized as a structured Call trace rooted at a Call whose `op_name` is the `Evaluation.evaluate` Op. The trace's shape — root `Evaluation.evaluate` → per-row `Evaluation.predict_and_score` children → each with `<Model>.predict` + `<Scorer Op>` grandchildren → sibling `Evaluation.summarize` — is what the W&B UI's *Evaluations* page (`/weave/evaluations`) recognises, and what `POST /v2/{entity}/{project}/eval_results/query` aggregates over. "Finishing" the run means calling `/call/end` on the root with `summary.weave.status="success"`. A wire-level EvaluationRun object exists (`POST /v2/{entity}/{project}/evaluation_runs`), but the cookbook does not use it — neither does the Python SDK; the structured Call trace alone suffices. **Distinct from the *Evaluation Definitions* page (`/weave/evaluation-definitions`) which lists Evaluation objects (the definitions, not the runs).**
_Avoid_: "evaluation run" (lowercase, ambiguous) when discussing the call-trace specifically; the call-trace interpretation is what the cookbook teaches.

**Eval Result**:
The output rows from an Evaluation Run — per-row predictions joined with Scorer Op outputs. Queried via `POST /v2/{entity}/{project}/eval_results/query` with `evaluation_call_ids=[<root call id>]`. Returns aggregated rows plus a `scorer_stats` summary (per-scorer pass rates, value types, trial counts).

## Why these terms matter

The Python SDK uses different vocabulary in places — for example, `weave.Evaluation` conflates the object and the run lifecycle. The service API is explicit: object first, then run. Using the service API directly means using the service API's vocabulary.
