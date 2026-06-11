# Contributing

## Commit conventions

This repo uses [Conventional Commits](https://www.conventionalcommits.org/) with explicit per-file staging.

Format:

```
<type>(<scope>): <subject>

<body>

Closes #<issue>
```

**Types:**

- `feat` — new recipe or new capability
- `fix` — bug fix
- `docs` — `README.md`, `CONTEXT.md`, `docs/adr/`, language-directory READMEs
- `chore` — config, repo-meta changes
- `ci` — CI workflow changes
- `refactor` — restructure without behavior change

**Scopes:**

- `python` — recipes under `python/`
- `ruby` — recipes under `ruby/`
- `dotnet` — recipes under `dotnet/`
- `golang` — recipes under `golang/`
- `java` — recipes under `java/`
- `docs` — documentation (`README.md`, `CONTEXT.md`, ADRs, language READMEs)
- `ci` — CI configuration

### Subject line

- Imperative mood ("add" not "added")
- No capital after the colon
- No trailing period
- ≤72 chars

### Staging

Use explicit `git add <path>` per file. No `git add .` or `-A`.

### Footer

Reference issues with `Closes #<n>` or `Fixes #<n>` — one per line.

## Adding a recipe

A recipe must:

1. Exist in **every** language directory with the same number and topic. (The `golang/` and `java/` ports are completing parity with the v1 set under [#43](https://github.com/ijdoc/weave-service-api-cookbook/issues/43); until that closes, a recipe number may be present in some directories ahead of others.)
2. Be a single self-contained script: env-var setup → HTTP call(s) → inline `# --- verification ---` block → exit 0 on success.
3. Use only the language's stdlib HTTP client plus minimal idiomatic helpers (`requests` in Python, `Net::HTTP` in Ruby, `HttpClient` in C#, `net/http` in Go, `java.net.http.HttpClient` + Jackson in Java). No client wrappers, no SDKs.
4. Be exercisable from CI: must run to exit 0 in under 60 seconds against a live W&B test project given only `WANDB_API_KEY`, `WANDB_ENTITY`, `WANDB_PROJECT` env vars.

### Input key convention

For Q&A-style traces, use `question` as the inputs key — matches recipes 01–04 and keeps simple examples readable across the cookbook. Recipes with structured inputs (retrieval queries, scorer payloads, evaluation rows) use shape-appropriate keys instead. Consistency across language ports of the same recipe is the hard rule; the Q&A default is a soft convention for the easy cases.

### Trace attribute convention

Every Call a recipe creates must set these `attributes` on `/call/start`:

| Attribute | Value | Notes |
|---|---|---|
| `cookbook.language` | `"python"`, `"ruby"`, `"dotnet"`, `"golang"`, or `"java"` | Matches the language directory the recipe lives in. |
| `cookbook.recipe` | recipe id, e.g. `"01_start_call"` | snake_case, same across all language ports of the recipe (so `01_StartCall.cs` and `01_StartCall.java` still use `"01_start_call"` here). |
| `cookbook.environment` | `"dev"` or `"ci"` | Sourced from the `COOKBOOK_ENVIRONMENT` env var, defaulting to `"dev"` when unset. CI sets it to `"ci"` so CI-created traces are filterable separately from local dev traces in the W&B UI. |

These let you filter traces by language, recipe, or environment in the W&B UI, and the verification block should assert each one as part of the round-trip check. The `cookbook.*` namespace is reserved for this repo's conventions — don't put anything else under it.

### Object id convention

Weave Objects a recipe creates (Op, Model, Scorer, Dataset, Evaluation) use a per-language `object_id`: `recipe-NN-<topic>-<lang>` — e.g. `recipe-09-is-correct-golang`, `recipe-11-eval-java`. The `-<lang>` suffix (`python`/`ruby`/`dotnet`/`golang`/`java`, matching `cookbook.language`) keeps each port's Objects distinct so they version independently (see [ADR-0004](docs/adr/0004-source-language-reference-in-source-code.md)). Call `op_name`s for recipes that create no Object (e.g. `recipe-01-start-call`) are **shared** across ports — each port emits its own Calls under the same `op_name`, distinguished by the `cookbook.language` attribute.

The canonical Eval Op names (`Evaluation.evaluate`, `Evaluation.predict_and_score`, `Evaluation.summarize`) are the exception: they stay byte-identical across all languages because `/eval_results/query` filters on those exact capital-case strings (see [ADR-0005](docs/adr/0005-imperative-sdk-path.md)).

## Adding language support

A new language must replicate the **full** recipe sequence, not a subset — a partial language is worse than no language ([ADR-0002](docs/adr/0002-language-major-layout.md)). Propose it via an issue first. The v1 set (Python / Ruby / .NET) was followed by Go (`golang/`) and Java (`java/`), tracked under [#43](https://github.com/ijdoc/weave-service-api-cookbook/issues/43); each port carries over the trace-attribute, object-id, and ADR-0004/0005 conventions above. Per-language toolchain and dependency conventions live in that language directory's `README.md`.
