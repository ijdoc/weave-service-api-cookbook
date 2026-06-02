# 5. Use the imperative SDK path (structured Call traces + Op-based scorers)

Date: 2026-06-02

## Status

Accepted

## Context

The Weave service exposes multiple seemingly-natural endpoints for evaluation
work, and not all of them produce artifacts the rest of the system actually
consumes. The cookbook has to pick one consistent path.

The endpoints we considered:

- `POST /v2/{entity}/{project}/evaluation_runs` — a declarative "Evaluation
  Run" object with its own lifecycle (`/evaluation_runs/{id}/finish`).
- `POST /v2/{entity}/{project}/scorers` — a dedicated Scorer Object family
  with `op_source_code` as the body's load-bearing field.
- `POST /v2/{entity}/{project}/evaluations` — Evaluation Object creation
  that also *auto-creates* per-eval-aliased Ops (`<eval-id>.evaluate`,
  `<eval-id>.predict_and_score`, `<eval-id>.summarize`).
- `POST /v2/{entity}/{project}/ops` — Op creation with required `name` +
  `source_code` body.
- `POST /obj/create` — the generic Object endpoint.
- `POST /file/create` + `POST /obj/create` — the two-step Op-registration
  flow the Python SDK uses internally.

The cookbook is one client on this service-API surface; the Python SDK is
another. The SDK uses a particular subset that we call the *imperative
path*:

1. Evaluation Objects via `/obj/create`, not `/v2/.../evaluations`.
2. Scorers as plain Ops via `/file/create` + `/obj/create`, not the
   dedicated Scorer-Object family.
3. Eval runs are *structured Call traces* (rooted at a Call whose `op_name`
   is the canonical `Evaluation.evaluate` Op ref), not `/v2/.../evaluation_runs`
   objects.

This wasn't obvious from documentation. We discovered it empirically:

- Read back what `weave.Evaluation(...).evaluate(model)` actually emits on the
  wire — only Call/start/end, /obj/create, /file/create.
- Probed `/v2/.../evaluation_runs` standalone and found it works but no
  downstream consumer (Evaluations UI, `/eval_results/query`) pays attention.
- Probed `/v2/.../evaluations` and found the auto-created Ops use
  per-eval-aliased names the aggregator doesn't recognise.
- Probed `/v2/.../ops` and found it *lowercases* `object_id`
  (`Evaluation.evaluate` → `evaluation.evaluate`), and the aggregator's
  filter is case-sensitive on the canonical capital-case names.

## Decision

The cookbook commits to the imperative SDK path across all evaluation-flow
recipes (07–13):

1. **Evaluation Object** (recipe 11) — `POST /obj/create` with
   `builtin_object_class="Evaluation"` and a hand-built val that mirrors what
   `weave.Evaluation(...)` produces.
2. **Canonical Eval Ops** (recipe 11) — `POST /file/create` (multipart) then
   `POST /obj/create` with `val = {_type: "CustomWeaveType", files: {"obj.py":
   <file digest>}, weave_type: {"type": "Op"}}`. This path preserves the
   `object_id` casing the aggregator filter requires.
3. **Scorer Op** (recipe 09) — same two-step pattern. Decoupled from any
   formal Scorer Object class.
4. **Evaluation Run** (recipe 12) — a structured 4-level Call trace built
   purely from `/call/start` + `/call/end`. No `/v2/.../evaluation_runs`
   object is ever created.
5. **Eval results** (recipe 13) — `POST /v2/{entity}/{project}/eval_results/query`
   with `evaluation_call_ids=[<root call id>]`. Aggregates over the
   structured trace alone.

## Consequences

**Positive:**

- **Matches the SDK shape end-to-end** — the W&B UI's Evaluations page, the
  Evaluation Definitions page, and `/eval_results/query` all recognise the
  cookbook's artifacts. Cookbook + SDK can interoperate in the same project
  (content-addressing collapses identical scaffolds into the same digests).
- **No new endpoint families** beyond ones already covered in recipes 01–11.
  Recipes 12 and 13 are pure `/call/*` and `/v2/.../eval_results/query`
  applications — every primitive was introduced earlier.
- **The full eval trace is human-debuggable** — the structured Call tree is
  visible in the trace UI without any "run-finish" lifecycle machinery to
  walk through.

**Negative:**

- **The cookbook can't lean on the more declarative-looking v2 endpoints.**
  Readers exploring the OpenAPI will see `/v2/.../evaluation_runs` and
  `/v2/.../scorers` and need this ADR to know why the cookbook avoids them.
- **`/v2/.../ops` (the natural Op endpoint) is also avoided.** The
  case-lowercasing trap is invisible until you query results; it warrants the
  more involved `/file/create` + `/obj/create` flow for canonical Ops where
  case matters.

## Alternatives considered

- **Use `/v2/.../evaluation_runs` for the run lifecycle.** Rejected: the SDK
  doesn't, and the run-finish endpoint adds machinery the UI doesn't actually
  read.
- **Use `/v2/.../scorers` for scorers (formal Scorer Object).** Rejected:
  requires Python source code as a typed body field, which is awkward for
  non-Python cookbook languages and disagrees with how the SDK actually
  stores scorers (as Ops, not Scorer Objects).
- **Use `/v2/.../evaluations` for the Evaluation Object.** Rejected:
  auto-creates per-eval-aliased Ops that `/eval_results/query` doesn't match
  — empirically verified.
- **Use `/v2/.../ops` for canonical Eval Ops.** Rejected: lowercases the
  `object_id`, breaking the case-sensitive aggregator filter.
- **Two paths (declarative for some artifacts, imperative for others).**
  Rejected: half the value of the cookbook is consistency. Mixing paths
  would force every recipe reader to track which artifact uses which family.
