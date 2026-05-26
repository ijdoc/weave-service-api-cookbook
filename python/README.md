# Python recipes

Raw-HTTP recipes for the Weave service API using `requests`.

## Setup

These recipes use [PEP 723 inline script metadata](https://peps.python.org/pep-0723/), so each script declares its own dependencies. The recommended runner is [`uv`](https://docs.astral.sh/uv/):

```bash
# Install uv (one-time, per machine)
curl -LsSf https://astral.sh/uv/install.sh | sh

# Set env vars (see ../README.md#setup)
export WANDB_API_KEY=...
export WANDB_ENTITY=...
export WANDB_PROJECT=weave-service-api-cookbook

# Run any recipe
uv run python/01_start_call.py
```

`uv run` reads the PEP 723 block at the top of each script, materializes a transient virtualenv with the declared dependencies, and runs the script. No `pip install`, no requirements file, no activated venv needed.

If you'd rather use plain `pip`, every recipe only needs `requests`:

```bash
python -m venv .venv && source .venv/bin/activate
pip install requests
python python/01_start_call.py
```

## Conventions

- Each recipe is a single file, runnable end-to-end with exit 0 on success.
- Each recipe ends with an inline `# --- verification ---` block that reads the just-written state back through the API and asserts on it.
- Auth: `requests.post(..., auth=("api", os.environ["WANDB_API_KEY"]))` — HTTP basic auth with literal username `api` and the API key as password.
- All recipes target `https://trace.wandb.ai` unless `WEAVE_SERVICE_URL` overrides. Note: this is intentionally **not** `WANDB_BASE_URL` — that env var is reserved by the W&B SDK for the core API (`api.wandb.ai`), which is a different host.

See [`../CONTRIBUTING.md`](../CONTRIBUTING.md) for the recipe contract.
