# Penghou.Siming

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
- The v1 suite uses unkeyed SHA-256. Planned evolution distinguishes public
  ledger-context binding from optional externally keyed hashing; neither will
  reinterpret v1 history or replace trusted checkpoints.

## Planned usage

```csharp
IAppendOnlyLedger ledger = /* Penghou.Siming.Sqlite */;

var entry = await ledger.AppendAsync(
    streamId: sessionId,
    eventType: "WorkspacePromoted",
    canonicalPayload: payloadBytes);

var checkpoint = await LedgerCheckpoints.CaptureAsync(ledger);
var verification = await LedgerVerifier.VerifyAsync(
    ledger,
    checkpoint,
    new LedgerVerificationOptions(PageSize: 1000, Progress: progress),
    cancellationToken);
```

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

## Repository layout

```text
src/Penghou.Siming
src/Penghou.Siming.Sqlite
src/Penghou.Siming.Cryptography
src/Penghou.Siming.Testing
src/Penghou.Siming.Verify
tests/Penghou.Siming.Tests
tests/Penghou.Siming.Sqlite.Tests
docs
```

Start with the [implementation plan](docs/implementation-plan.md), then see the
[unfinished roadmap](docs/roadmap.md) and
[persistence contract](docs/persistence-contract.md).
