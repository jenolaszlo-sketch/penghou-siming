# Penghou.Siming

[![CI](https://github.com/jenolaszlo-sketch/penghou-siming/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/penghou-siming/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/penghou-siming)](LICENSE)

Penghou.Siming is a small, embedded, tamper-evident event ledger for .NET.
It combines a versioned cryptographic hash-chain format with transactional
SQLite persistence for local-first audit and provenance workloads.

Siming is intended for AI-assisted engineering sessions, workflow audit trails,
tool execution provenance, approvals, decisions, workspace promotions, and other
histories where committed events must remain ordered and independently
verifiable.

```text
canonical payload bytes
        + ordered event envelope
        + previous row hash
        -> SHA-256 row hash
        -> atomic SQLite append
```

Siming is tamper-evident, not tamper-proof. A party that can replace the entire
database can also recompute a replacement chain. Detecting rewriting or rollback
therefore requires comparison with a previously trusted checkpoint, optionally
anchored in Git, signed, or stored elsewhere.

## Packages

| Package | Responsibility |
| --- | --- |
| `Penghou.Siming` | Cryptographic format, canonical payload contracts, verification, ledger heads, and checkpoints |
| `Penghou.Siming.Sqlite` | SQLite persistence, atomic transactions, writer serialization, crash recovery, and append-only enforcement |
| `Penghou.Siming.Cryptography` | Optional detached Ed25519 checkpoint signing and public-key verification |
| `Penghou.Siming.Testing` | Provider-neutral conformance checks reusable by SQLite and future storage providers |
| `Penghou.Siming.Verify` | Standalone SQLite ledger and checkpoint verification CLI |

The projects currently target .NET 8. Multi-targeting and packaging policy will
be finalized before the first preview release.

## Design principles

- SQLite owns storage, transactions, locking, and crash recovery.
- Siming owns ordering, integrity, verification, and checkpoint semantics.
- The core cryptographic layer operates on canonical bytes, not CLR objects.
- Hash input is versioned, length-delimited, and independently reproducible.
- Normal APIs expose append and read operations but no historical mutation.
- SQLite triggers protect against accidental `UPDATE` and `DELETE`; cryptographic
  verification remains the real integrity boundary.
- A global sequence and hash chain are authoritative. Streams are logical query
  partitions and do not form independently verifiable chains in v1.
- Optional idempotency keys are globally unique per ledger, cryptographically
  committed, atomically deduplicated, and directly queryable by every conforming
  provider.
- Optional expected-head appends provide atomic optimistic concurrency. An
  identical idempotent retry still resolves to its original entry before the
  head condition is evaluated.
- Sensitive payload retention is a caller policy. Applications should prefer
  bounded provenance and content identities over raw secrets or model payloads.
- Append inputs are bounded before persistence. Defaults are conservative and
  each provider accepts the same provider-neutral `LedgerInputLimits` policy.
- The v1 suite uses unkeyed SHA-256. Epoch 2 binds a public ledger context and
  epoch 3 adds the optional externally keyed `hmac-sha256-v1` suite; neither
  reinterprets earlier history nor replaces trusted checkpoints.

## Quick start

```csharp
await using var ledger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializerV2>(
    new SimingSqliteOptions { DatabasePath = "session-ledger.db" }, new());

var entry = await ledger.AppendAsync(new LedgerAppendRequest<WorkspacePromoted>(
    sessionId, "WorkspacePromoted", new WorkspacePromoted(/* ... */)));

var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
var verification = await ledger.VerifyAsync(checkpoint);
```

See `samples/Penghou.Siming.Samples` for a runnable tour covering idempotency,
pagination, signed checkpoints, context-bound epochs, and the keyed suite.

Typed payloads can use `CanonicalJsonPayloadSerializer` (the historical v1
contract) or `CanonicalJsonPayloadSerializerV2`. The v2 contract rejects
duplicate object names and canonicalizes number tokens without a lossy
`double`/`decimal` conversion. Both are named contracts protected by portable
JSON/hash vectors; existing v1 rows and Guyabano hashes are never
reinterpreted. Its `ComputeSha256` and `VerifySha256` helpers derive logical
identity from either a `JsonElement` or persisted UTF-8 JSON. Exact envelope or
file-byte integrity remains a separate hash. Configure stricter append limits
when appropriate:

```csharp
var options = new SimingSqliteOptions
{
    DatabasePath = "session-ledger.db",
    InputLimits = LedgerInputLimits.Default with
    {
        MaxPayloadBytes = 1024 * 1024,
        MaxEventTypeUtf8Bytes = 128
    }
};
```

Text limits measure encoded UTF-8 bytes, not UTF-16 characters. Limit failures
occur before SQLite opens a write transaction and expose the rejected field,
actual byte count, and configured maximum.

Verification captures the target head and processes bounded pages without
retaining the complete ledger. Cancellation is checked between reads and rows;
progress is reported at page boundaries.

To verify a SQLite database from a terminal:

```powershell
dotnet run --project src/Penghou.Siming.Verify -- ledger.db --checkpoint checkpoint.json
```

The CLI also accepts `--signed-checkpoint`, `--public-key`, and `--key-id`.
It prints one JSON result to standard output, progress to standard error, and
returns `0` for valid, `1` for verification failure, `2` for invalid input, or
`130` for cancellation. It opens the database through a strict read-only path,
performs no initialization or schema mutation, and verifies one stable SQLite
read snapshot while writers may continue under WAL.

## Install

```powershell
dotnet add package Penghou.Siming --prerelease
dotnet add package Penghou.Siming.Sqlite --prerelease
dotnet tool install --global Penghou.Siming.Verify --prerelease
```

Add `Penghou.Siming.Cryptography` only when detached Ed25519 checkpoint signing
is required. Provider authors can use `Penghou.Siming.Testing` to run the shared
behavioral conformance suite.

## Build and publish

CI builds, checks formatting, runs all .NET tests, independently reproduces the
v1 ledger and v2 canonical-JSON vectors in Python, then restores, builds, and vulnerability-audits an
isolated consumer from the packed artifacts before uploading `.nupkg` and
`.snupkg` files.

Publishing follows the Baize workflow. Configure NuGet trusted publishing for
this GitHub repository and workflow, then add the NuGet account name as the
`NUGET_USER` repository secret. Either dispatch **Publish to NuGet** manually or
push a version tag; a tag such as `v0.1.0-preview.4` becomes package version
`0.1.0-preview.4`.

```powershell
git tag v0.1.0-preview.4
git push origin v0.1.0-preview.4
```

The API remains pre-release and may change before the first package release.

## Current status

The current package line is `0.1.0-preview.6` on .NET 8. The in-memory and
SQLite ledgers, provider conformance suite, canonical JSON v1 and v2 identities,
expected-head appends, bounded verification, detached Ed25519 checkpoints,
context-bound (epoch 2) and keyed `hmac-sha256-v1` (epoch 3) ledger epochs, and
the verification CLI (checkpoint, signed-checkpoint, context, and HMAC key
flags) are implemented. Runnable API examples live in `samples/` and run in CI.
Remaining roadmap work includes expanded platform CI, Native AOT review,
benchmarks, operational guidance, and Guyabano adoption.

## Samples

`samples/Penghou.Siming.Samples` is a runnable end-to-end tour: SQLite appends
with idempotency and pagination, checkpoint export/import/verification, signed
checkpoints, context-bound epoch-2 ledgers, and keyed epoch-3 ledgers with a
wrong-secret failure. Every scenario asserts its outcome:

```powershell
dotnet run --project samples/Penghou.Siming.Samples -c Release
```

## Repository layout

```text
src/Penghou.Siming
src/Penghou.Siming.Sqlite
src/Penghou.Siming.Cryptography
src/Penghou.Siming.Testing
src/Penghou.Siming.Verify
samples/Penghou.Siming.Samples
tests/Penghou.Siming.Tests
tests/Penghou.Siming.Sqlite.Tests
docs
tools
vectors
```

Start with the [implementation plan](docs/implementation-plan.md), then see the
[unfinished roadmap](docs/roadmap.md) and
[persistence contract](docs/persistence-contract.md).

## License

Apache-2.0

Copyright (c) 2026 Jenő Konrád László
