# 2. Language-major layout with parallel numbering

Date: 2026-05-26

## Status

Accepted

## Context

A multi-language cookbook can be organized two ways:

- **Language-major**: top-level directories per language (`python/`, `ruby/`, `dotnet/`), each containing the full recipe sequence.
- **Topic-major**: top-level directories per concept (`tracing/start-call/`, `evaluation/create/`), each containing a script per language.

The audience is "I have an application in language X and I need to call the Weave service API in X." They want to stay in one language while reading, not switch context between three on every page.

## Decision

Language-major layout. Each language directory contains the full recipe sequence with parallel numbering:

```
python/01_start_call.py
ruby/01_start_call.rb
dotnet/01_StartCall.cs
```

A recipe with number N covers the same topic across all three languages.

The top-level `README.md` and `CONTEXT.md` carry language-agnostic concepts and ADRs. Each language directory has its own `README.md` covering setup (interpreter, package manager, env vars).

## Consequences

**Positive:**

- Readers stay in one language directory throughout.
- The parallel-numbering invariant forces honest cross-language parity. Adding endpoint X means adding it everywhere.
- Issues decompose cleanly along language seams.

**Negative:**

- Side-by-side comparison ("how does endpoint X look in Python vs Ruby?") requires opening two files. Acceptable: comparison is a secondary use case for this cookbook.
- Adding a new language is high-cost — it must replicate the full recipe sequence, not just one example. We treat this as a desired property (a partial language is worse than no language).
