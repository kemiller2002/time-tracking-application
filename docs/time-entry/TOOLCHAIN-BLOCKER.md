---
id: TE-BLK-001
title: .NET/F# Toolchain Unavailable in Execution Environment
status: blocked
version: 0.1.0
created: 2026-09-11
updated: 2026-09-11
work_items: [WI-0009]
tags: [blocker, toolchain, fsharp, environment]
---

# Blocker: .NET/F# toolchain unavailable

## Statement

The F# compiler cannot be obtained in this execution environment. No `dotnet`
SDK is installed, and the environment's egress policy denies every host from
which one is distributed. F# therefore **cannot be compiled, tested, or built
to WebAssembly here**.

This is an environment capability gap, not a domain ambiguity and not a build
failure to debug.

## Evidence

### 1. No toolchain present

```
$ which dotnet
(nothing)
$ dotnet --version
bash: dotnet: command not found
```

No `.fsproj`, `.sln`, or `.fs` file exists anywhere in the repository either —
verified by `find . -name '*.fsproj' -o -name '*.sln' -o -name '*.fs'`
excluding `node_modules`. The F# application does not yet exist in any form.

### 2. Official installer blocked

```
$ curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
$ ./dotnet-install.sh --channel 9.0 --install-dir "$HOME/.dotnet"
curl: (56) CONNECT tunnel failed, response 403
```

The agent proxy's own status endpoint records the denial:

```json
"recentRelayFailures": [
  {
    "kind": "connect_rejected",
    "detail": "gateway answered 403 to CONNECT (policy denial or upstream failure)",
    "host": "builds.dotnet.microsoft.com:443"
  }
]
```

### 3. Every .NET distribution host is denied; other hosts are not

HTTP status by host (`000` = CONNECT refused, no TLS session established):

| Host | Result |
|---|---|
| `builds.dotnet.microsoft.com` | `000` — denied |
| `dotnetcli.azureedge.net` | `000` — denied |
| `dotnetcli.blob.core.windows.net` | `000` — denied |
| `aka.ms` | `000` — denied |
| `download.visualstudio.microsoft.com` | `000` — denied |
| `github.com` | `400` — reachable |
| `objects.githubusercontent.com` | `404` — reachable |
| `registry.npmjs.org` | `200` — reachable |

The proxy's `noProxy` allowlist covers `registry.npmjs.org`, `jsr.io`,
`pypi.org`, `files.pythonhosted.org`, `index.crates.io`, and
`proxy.golang.org` — no Microsoft or .NET host.

This is corroborated independently: ROS's own F# CLI (`./ros`) works here only
because `tools/ros_fs_launcher.mjs` downloads a **pre-built self-contained
binary from GitHub Releases**, never from a Microsoft host. That binary
bundles a .NET *runtime*; it contains no compiler and cannot build F#.

### 4. No viable npm-vendored substitute

| Package | Version | Why it does not work |
|---|---|---|
| `dotnet-sdk` | 1.3.2 | Downloads .NET Core **2.0.0** from the blocked Microsoft hosts at install time. 2.0.0 also long predates any WASM support. |
| `dotnet` | 1.1.4 | A JavaScript library, not the SDK. |
| `fable-compiler` | 2.13.0 | Requires a .NET SDK to run; also years out of date. |
| `@dotnet/sdk`, `node-dotnet-sdk` | — | Do not exist (404 on registry). |

## Scope of impact

**Blocked** (requires compilation or test execution):

- `dotnet restore` / `dotnet build` / `dotnet test` — the §30 build gates
- F# → WASM build
- TE-006 WASM/browser boundary wiring
- TE-008..TE-014 vertical slices (no slice can be *verified*)
- TE-016 heterogeneous verification of F# code
- SDE mechanical architecture checks of the F# tier boundary
  (the `check-semantic-architecture.sh` analogue cited in
  `.sde/architecture/FOUR-TIER-ARCHITECTURE.md`)

**Not blocked** (no compiler required):

- TE-001 discovery
- TE-002 requirements normalization and traceability
- TE-003 domain state model design
- TE-004 time-unit domain design
- TE-007 UI provenance recovery
- Decision records, open questions, ROS work representation

## Why no workaround was adopted

Three were considered and rejected:

1. **Rewrite the domain in JavaScript/TypeScript.** Directly forbidden: "Do
   not rewrite the application into JavaScript or TypeScript. The application
   is F# first." Also violates TE-R-090/TE-R-091.
2. **Fetch a .NET SDK from an unofficial GitHub mirror.** GitHub is reachable,
   so this is technically possible, but it means executing an unverified
   third-party toolchain binary. Rejected as an unreviewed supply-chain risk
   taken on the repository's behalf without authorization.
3. **Author F# and report the slices complete.** Rejected outright. `AGENTS.md`
   Core Rules: "Never fabricate evidence, file reads, approvals, commands, test
   results, or certainty" and "Do not claim a test passed unless it ran and
   passed." ROS's own rule is "Never mark work complete merely because code
   exists. Completion requires verification evidence."

## What is required to unblock

Any one of:

- Add `builds.dotnet.microsoft.com` (and `aka.ms` for the install script) to
  the environment's egress allowlist; then
  `./dotnet-install.sh --channel 9.0` succeeds and every blocked item becomes
  executable.
- Provision an environment image with the .NET 9 SDK and the
  `wasm-tools` workload pre-installed.
- Authorize a specific, named, checksum-pinned SDK source reachable from
  GitHub.

Recommended: the first — it is the smallest change and matches how the
repository already expects `./ros` and CI to work.
