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

1. Exist in **all three** language directories with the same number and topic.
2. Be a single self-contained script: env-var setup → HTTP call(s) → inline `# --- verification ---` block → exit 0 on success.
3. Use only the language's stdlib HTTP client plus minimal idiomatic helpers (`requests` in Python, `Net::HTTP` in Ruby, `HttpClient` in C#). No client wrappers, no SDKs.
4. Be exercisable from CI: must run to exit 0 in under 60 seconds against a live W&B test project given only `WANDB_API_KEY`, `WANDB_ENTITY`, `WANDB_PROJECT` env vars.

### Trace attribute convention

Every Call a recipe creates must set these `attributes` on `/call/start`:

| Attribute | Value | Notes |
|---|---|---|
| `cookbook.language` | `"python"`, `"ruby"`, or `"dotnet"` | Matches the language directory the recipe lives in. |
| `cookbook.recipe` | recipe id, e.g. `"01_start_call"` | snake_case, same across all language ports of the recipe (so `01_StartCall.cs` still uses `"01_start_call"` here). |

These let you filter traces by language or by recipe in the W&B UI, and the verification block should assert them as part of the round-trip check. The `cookbook.*` namespace is reserved for this repo's conventions — don't put anything else under it.

## Adding language support

Out of scope for v1. New languages should be proposed via an issue first.
