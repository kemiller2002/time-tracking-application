---
id: TE-BLK-001
title: .NET/F# Toolchain — Blocker RESOLVED
status: resolved
version: 0.3.0
created: 2026-09-11
updated: 2026-09-11
work_items: [WI-0009]
tags: [blocker, resolved, toolchain, fsharp, environment]
---

# Toolchain blocker — RESOLVED (twice, for two separate wrong claims)

**Resolution:** `apt-get install -y dotnet-sdk-8.0` installs .NET SDK
**8.0.131** from `archive.ubuntu.com` (Ubuntu 24.04 `noble-updates/main`). The
F# domain builds and its tests run. No allowlist change, no manual
installation, and no third-party mirror was needed.

```
$ dotnet --list-sdks
8.0.131 [/usr/lib/dotnet/sdk]

$ dotnet build TimeEntry.sln
Build succeeded.  0 Warning(s)  0 Error(s)

$ dotnet test TimeEntry.sln
Passed!  - Failed: 0, Passed: 67, Skipped: 0, Total: 67
```

## Correction to v0.1.0 of this document

**The previous version of this record was wrong, and its error is worth
stating plainly rather than quietly overwriting.**

v0.1.0 concluded that "no `dotnet` SDK can be obtained in this environment"
and recorded the blocker as environmental and unfixable without an allowlist
change. That conclusion was **too broad for the evidence behind it**.

What was actually tested was narrow: the `dot.net` install script and the
CDN hosts it pulls from (`builds.dotnet.microsoft.com`,
`dotnetcli.azureedge.net`, `aka.ms`, `download.visualstudio.microsoft.com`),
plus a handful of npm packages. Those findings were accurate — every one of
those hosts does refuse CONNECT with 403, and that is still true.

The mistake was generalising "the vendor CDN is blocked" into "no SDK is
obtainable", without testing the distribution channel this machine actually
has. Three channels were never probed:

| Host | Result | Consequence |
|---|---|---|
| `archive.ubuntu.com` | **200** | Ships `dotnet-sdk-8.0` 8.0.131 in `noble-updates/main` |
| `packages.microsoft.com` | **200** | Microsoft's own apt repo is reachable — the CDN block is not a Microsoft-wide block |
| `api.nuget.org` | **reachable** | `dotnet restore` works; all 4 projects restored |

The machine is Ubuntu 24.04 with a working `apt-get` and `ubuntu.sources`
configured. `apt-cache policy dotnet-sdk-8.0` would have shown the candidate
immediately. That check cost one command and was not run.

**Generalisable lesson:** "the vendor's download host is blocked" is a fact
about one host. "The toolchain is unobtainable" is a much stronger claim that
requires enumerating the channels the platform actually offers — OS package
manager first, since it needs no new egress. An environment-capability claim
should name the channels tested, and this one did not test the obvious one.

The two PPAs that *do* fail (`ppa.launchpadcontent.net` for deadsnakes and
ondrej/php) are unrelated to .NET and do not affect `apt-get install
dotnet-sdk-8.0`; `apt-get update` warns about them and proceeds.

## What the working toolchain immediately caught

Two real defects that no amount of careful reading had found, both in code
that looked correct:

1. **`Values.fs` — `FS0052`.** `DateTime(y, m, d).Ticks` accesses a member on
   a freshly constructed struct, producing an implicit-copy warning that
   `TreatWarningsAsErrors` promotes to an error. Fixed by binding the
   `DateTime` to a local first.
2. **`TransitionTests.fs` — `FS0001`, 13 occurrences.** `VoidEntryRequest` and
   `RestoreEntryRequest` have identical field sets, so F#'s record inference
   resolved the constructions to the later-declared type. Fixed with explicit
   type annotations at each construction site.

Neither was findable by inspection. This is the concrete cost of the period
when the domain could not be compiled, and the reason the work items were held
`blocked` rather than reported complete during it.

## Current toolchain constraints

- **SDK 8.0.131**, not 9.x. The Ubuntu archive carries 8.0 only. Tiers 1–3
  target `netstandard2.0` and are unaffected; `TimeEntry.Tests` targets
  `net8.0` for this reason.
- **`wasm-tools` is installed and WebAssembly builds work.** This was claimed
  blocked without being tested, and that claim was also wrong — see the second
  correction below.

## Second correction: the WASM workload was never blocked either

v0.2.0 of this document — the version that corrected the *first* mistake —
went on to assert that `wasm-tools` "is not available via apt" and "ships only
from the blocked vendor CDN", making the browser host the sole remaining gap.

That was wrong, by exactly the same reasoning error, one section after
diagnosing it. Workloads are **NuGet packages**, and `api.nuget.org` is
reachable — a fact already recorded in the table above.

```
$ dotnet workload install wasm-tools wasm-experimental
Successfully installed workload(s) wasm-experimental wasm-tools.

$ dotnet workload list
wasm-experimental   8.0.31/8.0.100   SDK 8.0.100
wasm-tools          8.0.31/8.0.100   SDK 8.0.100
```

The pattern is now unmistakable and worth naming plainly: **each time, a
blocker was asserted from the failure of one delivery channel without
enumerating the others.** First the installer CDN stood in for "no SDK", then
the same CDN stood in for "no workload", while apt and NuGet — both already
known reachable — went untested. The cost was real: work items sat blocked
that were executable the whole time.

The rule that would have prevented both: **an environment-capability claim
must name the channels tested. "The vendor's CDN is blocked" is a fact about
one host, never about a capability.**

## What the working WASM toolchain revealed

Two findings only a real build could produce:

1. **F# cannot host the WASM JS interop.** `[<JSExport>]` compiles in F# but
   does nothing: the attributes are consumed by a Roslyn source generator, so
   they are inert in an F# assembly. Verified by publishing an F# module with
   `[<JSExport>]` and probing it in Chromium — `getAssemblyExports` returned
   `{}`, no exports at all. Hence the single C# file in the repository
   (`browser/TimeEntry.Host/Program.cs`), which exists only to carry the
   attribute and forward a string.

2. **Reflection-based `System.Text.Json` blocks IL trimming.** The trim
   analyser reports `IL2026` against `Persistence.Serialization`, and
   `FSharp.Core` raises `IL2104`. Trimming is disabled with a stated reason
   rather than suppressed; WI-0030 tracks moving to source-generated
   serialization. The cost is bundle size only.

## Reproducing the install

```bash
apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get install -y dotnet-sdk-8.0
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet restore TimeEntry.sln
dotnet build   TimeEntry.sln --no-restore
dotnet test    TimeEntry.sln --no-build
```

The SDK is installed per-container and does not persist across a fresh
session, so CI and any new session must run the `apt-get install` step. It is
wired into `.github/workflows/ros-validation.yml` and `npm run check:fsharp`.
