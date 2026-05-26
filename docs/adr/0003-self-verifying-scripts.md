# 3. Self-verifying scripts, no separate test framework

Date: 2026-05-26

## Status

Accepted

## Context

Recipes need verification — at PR time (to catch breakage from service API changes) and at read time (so a reader running a recipe locally knows it worked). Three options were considered:

1. **Per-language test frameworks** (pytest, RSpec, xUnit) hitting live W&B.
2. **Mocked HTTP** with no live service.
3. **Self-verifying scripts**: each recipe ends with an inline verification block that queries the service API and asserts the just-written state landed.

Option 1 adds three different test toolchains and splits each topic into "the recipe" and "the test" — two files per concept. Option 2 inverts the cookbook's value proposition — readers would learn how to mock the API, not how to use it.

## Decision

Each recipe ends with an inline verification block (per-language equivalent of `# --- verification ---`). The block:

1. Uses the service API itself (typically `/calls/stream_query`, `/feedback/query`, `/eval-results/query`) to confirm the recipe's write actions landed.
2. Asserts the response shape.
3. Exits 0 on success, non-zero on failure.

CI is a single shell loop that invokes each script via its language runtime (`python`, `bundle exec ruby`, `dotnet run`). The contract is just exit code.

CI runs against a dedicated test entity + project. Secrets (`WANDB_API_KEY`, `WANDB_ENTITY`, `WANDB_PROJECT`) come from repo secrets.

## Consequences

**Positive:**

- Verification IS demonstration. Readers learn the query endpoints from the same script that uses the write endpoints — bonus pedagogy.
- No per-language test toolchain. A Ruby reader doesn't need RSpec; a .NET reader doesn't need xUnit.
- One CI configuration for all three languages.
- Recipes stay honest: the live service is the source of truth.

**Negative:**

- Every CI run creates real traces and evaluations. Mitigated by a dedicated CI project and a periodic cleanup workflow.
- PRs from forks can't access the secret, so live verification is skipped on those. Acceptable: maintainer rerun after pull catches breakage.
- Live-service dependency means CI can be transiently flaky if W&B has an outage. Acceptable for a docs-style repo.
