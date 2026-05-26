# .NET recipes

Raw-HTTP recipes for the Weave service API using `HttpClient` + `System.Text.Json` from the BCL.

## Setup

Requires .NET 10 SDK or newer. The recipes use [file-based programs](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/sdk#file-based-programs) (`dotnet run app.cs`), so each recipe is a single `.cs` file — no project file, no shared `.csproj`.

```bash
dotnet --version  # 10.x

# Set env vars (see ../README.md#setup)
export WANDB_API_KEY=...
export WANDB_ENTITY=...
export WANDB_PROJECT=weave-service-api-cookbook

# Run any recipe
dotnet run dotnet/01_StartCall.cs
```

## Conventions

- Each recipe is a single `.cs` file with top-level statements.
- File naming is PascalCase (`01_StartCall.cs`), matching .NET conventions. The `cookbook.recipe` attribute value is the snake_case form (`01_start_call`), shared across all language ports of the recipe.
- Each recipe ends with a `// --- verification ---` block that reads the just-written state back through the API and asserts on it. A `try/catch` around the assertions converts failures into a clean stderr message + exit code 1.
- Auth: HTTP basic via `AuthenticationHeaderValue("Basic", base64("api:<key>"))`.
- All recipes target `https://trace.wandb.ai` unless `WEAVE_SERVICE_URL` overrides. Note: this is intentionally **not** `WANDB_BASE_URL` — that env var is reserved by the W&B SDK for the core API (`api.wandb.ai`), a different host.
- Traces are tagged with `cookbook.language: "dotnet"` and `cookbook.recipe: "<id>"` per the convention in [`../CONTRIBUTING.md`](../CONTRIBUTING.md).

See [`../CONTRIBUTING.md`](../CONTRIBUTING.md) for the recipe contract.
