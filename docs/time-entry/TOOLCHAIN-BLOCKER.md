---
id: TE-BLK-001
title: .NET/F# Toolchain — Blocker RESOLVED
status: resolved
version: 0.2.0
created: 2026-09-11
updated: 2026-09-11
work_items: [WI-0009]
tags: [blocker, resolved, toolchain, fsharp, environment]
---

# Toolchain blocker — RESOLVED

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
- **`wasm-tools` workload is not installed and is not available via apt.** The
  F# → WebAssembly build (TE-006) therefore remains blocked; that specific
  workload is delivered through the same blocked vendor CDN. This is now the
  *only* remaining toolchain gap, and it is narrower than the original claim:
  the domain compiles and is verified; only the browser host cannot yet be
  built.

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
