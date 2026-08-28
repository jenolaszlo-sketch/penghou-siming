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
  committed, and atomically deduplicated by every conforming provider.
- Sensitive payload retention is a caller policy. Applications should prefer
  bounded provenance and content identities over raw secrets or model payloads.
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
`130` for cancellation. It refuses a missing database. Until the roadmap's
strict read-only provider path is implemented, run it on a copy: opening an
existing incomplete database can invoke normal SQLite initialization.

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
