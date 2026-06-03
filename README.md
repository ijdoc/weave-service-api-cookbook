# weave-service-api-cookbook

Recipes for integrating with W&B Weave through the [service API](https://docs.wandb.ai/weave/reference/service-api) — raw HTTP, no SDK.

For when you want to use Weave from a language without an SDK (e.g., Ruby, C#), or from a stack where the SDK's conventions don't fit your application.

## Status

v1 is complete: the tracing + evaluation flow across Python, Ruby, and .NET, exercised end-to-end in CI against a live W&B project. See the [v1 milestone](https://github.com/ijdoc/weave-service-api-cookbook/milestone/1) for scope.

## What's in here

- **`python/`** — recipes in Python using `requests`.
- **`ruby/`** — recipes in Ruby using `Net::HTTP`.
- **`dotnet/`** — recipes in C# using `HttpClient` + `System.Text.Json`.

Each language directory contains the same numbered recipes covering the same endpoints in the same order. Pick your language and read top to bottom.

### Recipes (v1)

| # | Topic | Headline endpoint(s) |
|---|---|---|
| 01 | Start + finish a single Call | `/call/start`, `/call/end` |
| 02 | Query a Call by op_name | `/calls/stream_query` |
| 03 | Parent + child Calls (RAG-style trace) | `/call/start` with `parent_id` |
| 04 | Update a Call after finish | `/call/update` |
| 05 | Add Feedback to a Call | `/feedback/create` |
| 06 | Batch Feedback across Calls | `/feedback/create` (per-call note + scorer rows) |
| 07 | Publish a Prompt + reference it from a Call (with tags + aliases) | `/v2/.../prompts`, `/objs/.../tags`, `/objs/.../aliases` |
| 08 | Create a Model + use it in a trace | `/v2/.../models`, `/v2/.../ops` |
| 09 | Scorer Op + score a Call (apply_scorer pattern) | `/file/create` + `/obj/create`, `/feedback/create` (`wandb.runnable.*`) |
| 10 | Create a Dataset | `/v2/.../datasets`, `/table/query` |
| 11 | Set up an Evaluation Object | `/file/create` + `/obj/create` (`builtin_object_class="Evaluation"`) |
| 12 | Run an evaluation as a 4-level trace | `/call/start` × N + `/v2/.../eval_results/query` |
| 13 | Query evaluation results | `/calls/stream_query` + `/v2/.../eval_results/query` |

For language-agnostic concepts (Call, Trace, Evaluation Object, Evaluation Run, Feedback, scorer), see [`CONTEXT.md`](CONTEXT.md).

For architectural decisions (why no SDK, why language-major, how verification works), see [`docs/adr/`](docs/adr/).

## Setup

Every recipe assumes these env vars:

| Variable | Required | Default | Notes |
|---|---|---|---|
| `WANDB_API_KEY` | yes | — | Your W&B API key. Used as the basic-auth password; the username is the literal string `api`. |
| `WANDB_ENTITY` | yes | — | The W&B entity (user or team) that owns the test project. |
| `WANDB_PROJECT` | yes | — | The W&B project name. Recipes construct `project_id` as `<entity>/<project>`. |
| `WEAVE_SERVICE_URL` | no | `https://trace.wandb.ai` | Override for dedicated cloud or self-managed deployments. Distinct from `WANDB_BASE_URL`, which the W&B SDK uses for the core API (`api.wandb.ai`). |
| `COOKBOOK_ENVIRONMENT` | no | `dev` | Tags every Call this cookbook creates with `cookbook.environment` so dev and CI traces are filterable separately in the W&B UI. CI sets it to `ci`. See [`CONTRIBUTING.md`](CONTRIBUTING.md). |

Get your API key at https://wandb.ai/authorize. You don't need to create the project beforehand — W&B auto-creates a project on the first trace it receives.

Per-language setup (`uv`, `bundler`, `dotnet`) is documented in each language directory's README.

## Audience

You're building an application that needs Weave tracing and/or evaluation, and either:

- Your stack doesn't have a Weave SDK (Ruby on Rails, ASP.NET Core, …), **or**
- You want to bring your own client surface and need to understand the wire-level details first.

You will **not** find a Rails-specific or .NET-specific sample app here. The recipes are minimal scripts that exercise the API; how you wrap them in your framework is up to you.

## Verification

Every recipe ends with an inline verification block that uses the service API itself to confirm the action landed. The same recipes run in CI against a live W&B test project. See [ADR-0003](docs/adr/0003-self-verifying-scripts.md).

## License

Apache-2.0. See [LICENSE](LICENSE).
