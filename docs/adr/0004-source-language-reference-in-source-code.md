# 4. Embed source-language reference in Python-required source fields

Date: 2026-05-27

## Status

Accepted

## Context

The Weave service exposes three v2 endpoints that require uploading Python source code as a string field:

- `POST /v2/{entity}/{project}/ops` (`source_code`) — for Op objects (model predict, scorer functions, evaluation primitives, etc.).
- `POST /v2/{entity}/{project}/models` (`source_code`) — for Model objects.
- `POST /v2/{entity}/{project}/scorers` (`op_source_code`) — for Scorer objects.

These endpoints exist because the Python SDK uses them to register source-language artifacts for trace UI rendering, code-capture, and content-addressed versioning. The server stores the uploaded source verbatim, computes a content-addressed digest, and references it from the resulting object's val (typically as `files.obj.py`).

This cookbook is targeted at consumers calling these endpoints from languages that don't have a Weave SDK — Ruby, .NET, and any other stack where the SDK's Python-class conventions don't fit. Uploading "real" Python source for a model implemented in Ruby would be a lie: the actual implementation isn't Python, can't be executed by the W&B service, and the recipe never invokes the server-side execution path (`/evaluations/evaluate_model`) where that source would matter.

At the same time, the cookbook does need each language's Model / Scorer / Op to:

1. Show up correctly in the W&B UI (which expects a recognizable Python shape for some views).
2. Version naturally — when the underlying source-language code changes, the Weave digest should change, and the new state should land as a new `version_index` of the same `object_id`.
3. Stay distinguishable across language ports (so a Ruby model and a .NET model with the same logical purpose are separate, traceable artifacts).

## Decision

Every Op, Model, and Scorer that a cookbook recipe creates uploads a minimal Python scaffold for its `source_code` field. The scaffold carries a source-language reference in **two complementary placements**: a header comment block (top-of-file, succinct visual identifier) and an in-method docstring (contextualised, self-explaining). Both placements duplicate the same `<language>` / `<path>` / `<SHA256>` triple, by design — they serve different UI contexts (source-viewer top-of-file vs method-detail panel).

Three load-bearing pieces of metadata, embedded in both placements:

1. **`<language>` tag**: matches the recipe's directory (`python` / `ruby` / `dotnet`). Surfaces in the W&B UI so a viewer can tell at a glance which port produced the object.
2. **Source path**: relative path to the recipe file (e.g., `ruby/08_use_model.rb`, `dotnet/08_UseModel.cs`). Lets a reader jump from the W&B UI back to the canonical source.
3. **SHA256 of the recipe file**: computed at runtime over the recipe's bytes. Gives content-addressed versioning a meaningful trigger — when the source-language recipe changes, the embedded SHA changes, the Python scaffold's bytes change, and Weave's `(digest, version_index)` correctly tracks the new version.

## Scaffold structure

```python
# Cookbook scaffold (<language>)
# Source: <relative path>
# SHA256: <digest>

import weave


class Model(weave.Model):    # or analogous shape per object kind
    answer: str = "yes"      # parametrized values (any JSON type or weave:// ref)

    @weave.op
    def predict(self, question):
        """The actual predict implementation lives in:
            <relative path>

        Byte-for-byte reference (SHA256 of the recipe file):
            <digest>

        This Python op is a metadata handle, not the real model —
        running it raises NotImplementedError by design.
        """
        raise NotImplementedError(
            "This op is a Python scaffold uploaded from a non-Python "
            "recipe. See the docstring above for the real source-language "
            "file and a verifiable byte-for-byte reference (SHA256)."
        )
```

**Class attributes** (e.g., `answer: str = "yes"`) carry the model's parametrised values — any JSON type, or a `weave://` ref to another Object. These DO render in the UI and represent the model's instance state honestly.

**Method body** never attempts to mirror the real implementation across language boundaries — that would be lossy translation. Instead, the in-method docstring re-states the `<path>` + `<SHA256>` with explicit framing ("the following SHA256 is a byte-for-byte reference to the real source-language file"), and the body raises `NotImplementedError` with the same explanation. This removes the "what's this SHA256?" friction for readers who land on the method-detail panel without seeing the file header.

## Scope

ADR-0004 applies to Weave Objects whose val carries an uploaded `source_code` (stored as `files.obj.py`):

- **Op** — every Op the cookbook creates.
- **Model** — Model objects created via `POST /v2/.../models`.
- **Scorer Object** — `POST /v2/.../scorers` (the cookbook avoids the Scorer Object endpoint in favour of Scorer Ops, but if it appeared in a future recipe the pattern would apply).

ADR-0004 does **not** apply to:

- **Prompt** — val is data (`content`, `description`), no source_code field.
- **Dataset** — val is data (`rows` ref), no source_code field.
- **Evaluation Object** — val is refs to other Objects, no source_code field.

For source_code-free Objects, the cookbook embeds source-language references in the val's `description` field instead when traceability is desirable — but that's a soft convention, not the ADR-0004 pattern.

## Consequences

**Positive:**

- **One pattern, three objects.** The same `<language>` + path + SHA convention applies uniformly to Op, Model, and Scorer `source_code` fields. Readers only learn it once.
- **Content-addressed versioning works as designed.** Edits to a recipe naturally bump the version (the SHA changes), no manual `version` bookkeeping needed. Re-running an unchanged recipe is idempotent (same SHA → same digest → same `version_index`). This is a strict improvement over the timestamped-name approach used for Datasets in recipe 09 (which had to fight content-addressing because the rows themselves were stable).
- **Per-language identity is automatic.** Each language port hashes a different file → different SHA → different digest, even when names are identical.
- **Cross-team navigation.** Anyone viewing the object in the W&B UI sees exactly which language file produced it and where to find it.
- **No fake-Python lying about behavior.** The scaffold is honest metadata; comments explain that real invocation lives elsewhere.

**Negative:**

- **Not executable as the "real" model.** The cookbook never exercises Weave's server-side execution path (`/evaluations/evaluate_model`), so this is fine for the cookbook's scope. Readers porting this pattern beyond the cookbook should know that limit.
- **Schema-tightening risk.** If Weave ever validates `source_code` more strictly (e.g., requires it to import `weave` and define a proper subclass), the scaffold would need to evolve. The current scaffold already adopts the basic shape to minimize that risk; if it breaks, the recipes' verification blocks will catch it and we update the scaffold.
- **Whole-recipe hashing.** The SHA is computed over the entire recipe file. Any edit bumps the version — even pure-comment edits. For the cookbook this is desired (each recipe *is* the model proxy); in production, where model code lives in a separate file with stable boundaries, hashing just the model module is more accurate.
- **Duplicated SHA / path between header and docstring.** Both placements carry the same triple. Intentional: the header serves the source-viewer top-of-file scan, the docstring serves the method-detail panel and removes ambiguity for readers who land there without seeing the header. Sync risk is nil because both are generated from the same Python f-string at runtime.

## Alternatives considered

- **Upload source-language source verbatim** as the `source_code` string. Rejected: the W&B UI would render Ruby or C# as malformed Python; quoting/encoding edge cases are nasty; the source-code field is fundamentally Python-typed on the service side.
- **Upload an empty or `pass`-only scaffold** with no embedded reference. Rejected: loses cross-language traceability, and content-addressing would collapse identical empty scaffolds into one shared object across language ports — defeating per-language separation.
- **Hash a narrower portion of the recipe** (just the model logic). Rejected for the cookbook (each recipe *is* the focal artifact, so file-level granularity is right); kept as a note for readers adapting the pattern to larger codebases.
- **Reuse the Python SDK's auto-registered Ops** (already in the project from prior SDK runs) instead of creating per-language Ops. Rejected: makes the cookbook depend on the SDK having been run in the target project, breaking standalone reproducibility.
