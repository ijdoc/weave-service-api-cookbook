# Go recipes

Raw-HTTP recipes for the Weave service API using `net/http` + `encoding/json` from the standard library — no third-party modules.

## Setup

Requires Go 1.21 or newer. Each recipe is a single self-contained `package main` file with only stdlib imports, so `go run` works directly with no `go.mod`:

```bash
go version  # 1.21+

# Set env vars (see ../README.md#setup)
export WANDB_API_KEY=...
export WANDB_ENTITY=...
export WANDB_PROJECT=weave-service-api-cookbook

# Run any recipe
go run golang/01_start_call.go
```

## Conventions

- Each recipe is a single `package main` file using only the standard library (`net/http`, `encoding/json`, `time`, …). No third-party modules, so no `go.mod` is needed — `go run <file>` resolves against the stdlib alone.
- File naming is snake_case (`01_start_call.go`), matching the `python/` and `ruby/` ports. The `cookbook.recipe` attribute value is the same snake_case form (`01_start_call`), shared across all language ports of the recipe.
- Each recipe ends with a `// --- verification ---` block that reads the just-written state back through the API and asserts on it. Assertion failures route through a `fatal(...)` helper that writes to stderr and exits 1.
- Auth: HTTP basic via `req.SetBasicAuth("api", apiKey)`.
- All recipes target `https://trace.wandb.ai` unless `WEAVE_SERVICE_URL` overrides. Note: this is intentionally **not** `WANDB_BASE_URL` — that env var is reserved by the W&B SDK for the core API (`api.wandb.ai`), a different host.
- Traces are tagged with `cookbook.language: "golang"` and `cookbook.recipe: "<id>"` per the convention in [`../CONTRIBUTING.md`](../CONTRIBUTING.md). Weave Objects this port creates use the per-language `object_id` suffix `-golang` (e.g. `recipe-09-is-correct-golang`).

See [`../CONTRIBUTING.md`](../CONTRIBUTING.md) for the recipe contract.
