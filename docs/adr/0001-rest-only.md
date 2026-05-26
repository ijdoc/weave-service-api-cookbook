# 1. Service API only, no SDK

Date: 2026-05-26

## Status

Accepted

## Context

The W&B Weave Python SDK provides decorators and context managers (`weave.init()`, `@weave.op`, `weave.Evaluation`) that abstract away the underlying service API. For Python users this is the recommended path. But:

- Other languages don't have an SDK at all (Ruby, C#, Go, Rust, …).
- Some Python applications can't use the SDK as-is — frameworks that intercept import-time state, codebases with an existing tracing layer, environments where the added dependency is operationally costly.
- The existing [W&B cookbook for the service API](https://docs.wandb.ai/weave/cookbooks/weave_via_service_api) demonstrates only three endpoints (`/call/start`, `/call/end`, `/calls/stream_query`), all about tracing. There is no documented path for the evaluation flow via the service API in any language.

## Decision

This cookbook demonstrates the W&B Weave service API directly via HTTP, in three languages (Python, Ruby, C#). It does **not** use the `weave` SDK in any language, nor any first- or third-party client wrapper.

Python recipes use `requests`. Ruby recipes use `Net::HTTP` from stdlib. C# recipes use `HttpClient` + `System.Text.Json`.

## Consequences

**Positive:**

- Recipes serve as a wire-level reference. The wire format is the wire format; nothing is abstracted away.
- Portable: readers can translate any recipe into their language of choice by replacing the HTTP client.
- Honest: when the service API changes shape, recipes break in CI and we find out.

**Negative:**

- More boilerplate per recipe than the SDK would require. Mitigated by keeping recipes minimal.
- Where the SDK and the service API drift terminologically (e.g., `weave.Evaluation` conflating the object and the run), recipes have to be explicit. See [`CONTEXT.md`](../../CONTEXT.md).
- We don't benefit from any SDK convenience for new endpoints — every new endpoint is explicit recipe work.
