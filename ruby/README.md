# Ruby recipes

Raw-HTTP recipes for the Weave service API using `Net::HTTP` from stdlib.

## Setup

No gems required — every recipe uses only stdlib (`net/http`, `json`, `time`, `uri`). Any reasonably current Ruby works; we test against the system Ruby on macOS (2.6.x) and current 3.x.

```bash
ruby --version

# Set env vars (see ../README.md#setup)
export WANDB_API_KEY=...
export WANDB_ENTITY=...
export WANDB_PROJECT=weave-service-api-cookbook

# Run any recipe
ruby ruby/01_start_call.rb
```

## Conventions

- Each recipe is a single file, runnable end-to-end with exit 0 on success.
- Each recipe ends with an inline `# --- verification ---` block that reads the just-written state back through the API and asserts on it.
- Auth: `req.basic_auth("api", ENV["WANDB_API_KEY"])` — HTTP basic auth with literal username `api` and the API key as password.
- All recipes target `https://trace.wandb.ai` unless `WEAVE_SERVICE_URL` overrides. Note: this is intentionally **not** `WANDB_BASE_URL` — that env var is reserved by the W&B SDK for the core API (`api.wandb.ai`), which is a different host.
- Traces are tagged with `cookbook.language: "ruby"` and `cookbook.recipe: "<id>"` per the convention in [`../CONTRIBUTING.md`](../CONTRIBUTING.md).

See [`../CONTRIBUTING.md`](../CONTRIBUTING.md) for the recipe contract.
