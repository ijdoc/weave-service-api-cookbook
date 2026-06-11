# Java recipes

Raw-HTTP recipes for the Weave service API using the JDK's built-in `java.net.http.HttpClient` plus [Jackson](https://github.com/FasterXML/jackson) for JSON — the same "stdlib client + one minimal idiomatic helper" posture the other ports use (`requests` in Python, `Net::HTTP` in Ruby, `HttpClient` in .NET).

## Setup

The recipes run via [jbang](https://www.jbang.dev/), which turns a single `.java` file into a runnable script — the analog of .NET's file-based programs (`dotnet run app.cs`) and Python's PEP-723 inline dependencies. The Jackson dependency is declared inline with a `//DEPS` directive, and `//JAVA 17+` lets jbang fetch a matching JDK if one isn't already present.

```bash
# Install jbang (https://www.jbang.dev/download/)
curl -Ls https://sh.jbang.dev | bash -s - app setup   # or: brew install jbang
jbang version

# Set env vars (see ../README.md#setup)
export WANDB_API_KEY=...
export WANDB_ENTITY=...
export WANDB_PROJECT=weave-service-api-cookbook

# Run any recipe
jbang java/01_StartCall.java
```

The first run resolves Jackson (and, if needed, a JDK) into jbang's cache; subsequent runs are fast.

## Conventions

- Each recipe is a single `.java` file with a jbang header: a `//DEPS com.fasterxml.jackson.core:jackson-databind:<v>` line for JSON and `//JAVA 17+` for the language level. No build tool, no `pom.xml`/`build.gradle`.
- File naming is PascalCase (`01_StartCall.java`), matching Java/.NET conventions. The class is package-private (`class StartCall`) so the filename need not match a `public` class — jbang locates the `main` method regardless. The `cookbook.recipe` attribute value is the snake_case form (`01_start_call`), shared across all language ports of the recipe.
- Each recipe ends with a `// --- verification ---` block that reads the just-written state back through the API and asserts on it. Assertion failures route through a `fail(...)` helper that writes to stderr and exits 1.
- Auth: HTTP basic via an `Authorization: Basic <base64("api:<key>")>` header.
- All recipes target `https://trace.wandb.ai` unless `WEAVE_SERVICE_URL` overrides. Note: this is intentionally **not** `WANDB_BASE_URL` — that env var is reserved by the W&B SDK for the core API (`api.wandb.ai`), a different host.
- Traces are tagged with `cookbook.language: "java"` and `cookbook.recipe: "<id>"` per the convention in [`../CONTRIBUTING.md`](../CONTRIBUTING.md). Weave Objects this port creates use the per-language `object_id` suffix `-java` (e.g. `recipe-11-eval-java`).

See [`../CONTRIBUTING.md`](../CONTRIBUTING.md) for the recipe contract.
