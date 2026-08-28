# Penghou.Siming implementation plan

## Purpose

This is the primary resume document for Penghou.Siming. Update it as design
decisions or implementation evidence change. The shorter
[`roadmap.md`](roadmap.md) remains the milestone checklist; this document records
the reasoning, contracts, sequence, Guyabano adoption boundary, and open choices.

Last reviewed: **2026-08-28**

## Product definition

Penghou.Siming is a reusable, embedded, tamper-evident append-only event ledger
for .NET.

```text
Penghou.Siming
    cryptographic format
    payload serialization contracts
    canonical JSON convenience serializer
    verification
    heads and checkpoints

Penghou.Siming.Sqlite
    SQLite persistence
    transactions and writer serialization
    concurrency and crash recovery
    append-only enforcement

Penghou.Siming.Testing
    reusable provider conformance suite
    no dependency on SQLite

Penghou.Siming.Cryptography
    optional detached Ed25519 checkpoint signing
    no persistence responsibility

Penghou.Siming.Verify
    operational SQLite and checkpoint verifier CLI
```

SQLite owns storage, transactions, locking, recovery, and indexes. Siming owns
ordering, cryptographic integrity, verification, and checkpoints. It must not
become a database engine, blockchain, workflow engine, event-sourcing framework,
or distributed consensus system.

The initial consumer is Guyabano session history, but Siming must remain domain
neutral and useful to other coding harnesses, workflow engines, audit systems,
and provenance tools.

Repository: <https://github.com/jenolaszlo-sketch/penghou-siming>

## Accepted decisions

### Ledger and stream semantics

- One global monotonically increasing sequence is authoritative in v1.
- One global hash chain commits to the complete append order.
- `StreamId` is a logical query partition such as a Guyabano session ID.
- A stream-only result is not independently verifiable. Verification scans the
  global chain or verifies against a trusted global checkpoint.
- Sequence starts at 1 and is gap-free for committed rows.
- Empty head is sequence 0 with the deterministic genesis hash.

### Payload model

The core ledger accepts arbitrary definitive bytes. JSON is useful but is not a
requirement of the cryptographic engine.

```text
T payload
    -> configured ILedgerPayloadSerializer
    -> SerializedLedgerPayload
    -> store exact bytes
    -> hash exact bytes
```

The public API should offer both:

```csharp
ValueTask<LedgerEntry> AppendAsync(
    LedgerAppendRequest request,
    CancellationToken cancellationToken = default);

ValueTask<LedgerEntry> AppendAsync<T>(
    LedgerAppendRequest<T> request,
    CancellationToken cancellationToken = default);
```

The typed overload serializes before entering the storage/hash path. Verification
always operates on persisted bytes and never deserializes or requires the CLR
payload type.

The serializer result should be equivalent to:

```csharp
public sealed record SerializedLedgerPayload(
    ReadOnlyMemory<byte> Bytes,
    string ContentType,
    string SerializationFormat,
    int SerializationVersion);
```

These serializer descriptors are included in the row hash. This prevents a later
serializer change from silently reinterpreting old payloads.

Siming should ship:

- a raw-byte path with no transformation;
- a canonical JSON serializer;
- `AppendAsync<T>` using an explicitly configured serializer;
- byte-oriented reads;
- optional typed deserialization convenience that is never used for integrity
  verification.

Arbitrary payloads may be JSON, text, protobuf, MessagePack, compressed bytes,
encrypted bytes, or an application-defined format. The caller-provided bytes are
definitive.

### Canonical JSON

Canonical JSON is a convenience serializer and a reusable logical-content
identity tool, not a requirement for every ledger payload.

Siming uses a named Penghou canonical JSON contract rather than claiming RFC
8785 compliance. It matches Guyabano artifact hash contract `v2` and covers:

- ordinal property ordering;
- preserved array order;
- no insignificant whitespace;
- deterministic UTF-8 and string escaping;
- deterministic number representation;
- explicit null, boolean, string, array, and object handling.

Portable golden vectors contain input JSON, expected canonical JSON, and an
independently reproducible SHA-256 value. They lock compatibility before
Guyabano deletes its local canonicalizer. Existing Guyabano hashes must never be
reinterpreted.

### Input limits

- All providers enforce the same configurable `LedgerInputLimits` policy.
- Payloads and UTF-8 byte lengths of stream IDs, event types, content types,
  serialization formats, and idempotency keys are checked before persistence.
- Typed serialization output is subject to the same payload limit as raw bytes.
- A rejected append never advances the ledger head.
- Limits are operational policy and are deliberately not part of the v1 hash
  format, so providers and deployments may choose stricter bounds.

### Hash format

Do not hash ambiguous concatenation. The v1 hash input is a documented,
length-delimited binary envelope using fixed-width big-endian integers and UTF-8
strings. It commits at least to:

1. format version;
2. hash algorithm identity if it is not fixed by the format version;
3. immutable ledger ID;
4. global sequence;
5. stream ID;
6. ledger-assigned UTC commit timestamp as Unix epoch milliseconds;
7. event type;
8. content type;
9. serialization format and version;
10. optional application schema identity and version if accepted for v1;
11. exact payload length and persisted bytes;
12. optional idempotency key;
13. previous 32-byte SHA-256 hash.

The deterministic genesis algorithm and bytes are part of the persistence
contract. Golden vectors must make the format independently reproducible without
the original CLR types.

### Time

- `CommittedAt` is assigned by Siming inside the append transaction and included
  in the row hash.
- An application-controlled `OccurredAt` belongs in the payload or committed
  application headers. It is not a trusted clock guarantee.

### Append-only enforcement

- The normal API exposes no update or delete operation.
- SQLite triggers reject ordinary `UPDATE` and `DELETE` statements.
- Triggers are defense against accidents, not protection from a database owner.
- Schema migration never rewrites cryptographically committed rows.
- Incompatible persistence changes require a new format version or ledger epoch.

### Idempotency

- Append requests may carry an optional provider-neutral idempotency key.
- The key is globally unique within one ledger and is committed into the v1 row
  hash; absence is encoded distinctly as a signed length of `-1`.
- Repeating the same key with identical stream, event type, serializer
  descriptors, and definitive payload bytes returns the original committed
  entry without advancing the sequence.
- Providers expose direct lookup by idempotency key so callers can recover the
  original definitive envelope before constructing a retry.
- Reusing the key with different committed content throws
  `LedgerIdempotencyConflictException`.
- Providers enforce lookup and append atomically. SQLite uses its immediate
  transaction plus a filtered unique index; future providers must pass the same
  conformance check.

### Security and trust

Siming is tamper-evident, not tamper-proof.

Local verification detects malformed rows and modifications that were not
followed by consistent suffix recalculation. An attacker controlling the database
can rewrite the entire chain or restore an older valid database. Detection of
complete replacement or rollback requires comparison with a previously trusted
checkpoint retained elsewhere.

A valid hash chain proves historical consistency relative to a trusted head. It
does not prove:

- actor identity or authorization;
- that a payload is truthful;
- that the clock is accurate;
- confidentiality;
- that the database has not been replaced without an external checkpoint.

Payload classification and retention matter. Immutable prompts, responses, tool
outputs, and files may contain credentials or personal data. Consumers should
prefer bounded provenance and content identities over raw sensitive content.

### Checkpoints

A checkpoint contains ledger ID, sequence, head hash, creation time, and format
version. Verification should support:

- exact-head comparison;
- proving that the current ledger is a valid extension of an earlier checkpoint;
- detecting checkpoint/ledger identity mismatch;
- detecting truncation or older-backup restoration against a newer checkpoint.

Checkpoint storage, Git anchoring, and key management remain outside the core
persistence responsibility. Detached Ed25519 checkpoint signing is implemented
in the optional cryptography package and is never mandatory.

### Context binding and keyed hashing

The phrase “external salt” is split into two explicit future features:

1. **Public ledger context binding.** Canonical external context—application,
   environment, tenant, deployment, or similar identity—is digested and
   committed immutably. It prevents valid history from being transplanted
   between contexts but adds no secrecy.
2. **Optional keyed hashing.** A separately versioned suite such as
   `hmac-sha256-v1` uses a caller-managed secret. Persistence stores the suite
   and key ID, never the secret. This resists rewriting by an attacker holding
   only the database but creates key rotation, availability, backup, and
   recovery obligations.

Neither feature modifies v1, encrypts payloads, or replaces independently
retained checkpoints. Each new suite needs an explicit contract and independent
golden vectors.

## Immediate implementation sequence

1. Unify synchronous and paged verification behind one incremental state
   machine with precise failure categories.
2. Bound checkpoint/key inputs and hash large envelopes incrementally.
3. Make generated signing keys non-exportable by default and formalize external
   signer/key-store extension points.
4. Complete remaining adversarial-test coverage.
5. Specify public context binding and optional keyed suites without changing v1.
6. Establish package compatibility baselines and operational guidance, then
   begin Guyabano adoption.

## Package readiness

- [ ] public API baselines and compatibility policy;
- [ ] complete API usage examples;
- [ ] trimming and Native AOT review;
- [ ] CI for supported .NET targets and operating systems;
- [ ] append, pagination and full-verification benchmarks;
- [ ] threat model, payload retention, backup and checkpoint guidance;
- [ ] publish preview packages.

Exit criterion: Siming is independently usable rather than merely extracted
Guyabano code.

## Guyabano dogfooding and reduction

Create `Guyabano.Session.Sqlite` as the domain adapter. Guyabano will use one
independent Siming ledger file per session; a separate rebuildable catalog may
support discovery and current-state queries without becoming authoritative.

- [ ] map Guyabano `SessionEvent` to Siming streams and payloads;
- [ ] preserve event IDs, explicit sequence semantics where needed,
  idempotency keys, causation, correlation and cross-system references;
- [ ] replace and remove the unused filesystem session event store; no legacy
  migration is required before Guyabano has users;
- [ ] create rebuildable SQLite projections for timeline, pending input,
  current workspace, approval, and reconciliation state;
- [ ] prove projection rebuild, crash recovery, retry and pagination;
- [ ] optionally anchor session ledger heads in generated Git commits;
- [ ] remove Guyabano's duplicate canonical JSON only after golden compatibility
  vectors pass.

Exit criterion: Guyabano uses Siming as authoritative session history, while its
domain-specific projections remain replaceable and rebuildable.

## Package boundary with Guyabano

Move or centralize in Siming only generic mechanisms:

- canonical byte production and canonical JSON;
- versioned binary hash input and hash chaining;
- append-only storage and global ordering;
- verification and diagnostics;
- checkpoints;
- pagination, transactions, concurrency and crash recovery mechanics.

Keep in Guyabano:

- session event taxonomy and payload schemas;
- Zhinu workflow and saga state;
- Cangjie, Hetu, Baize and artifact references;
- workspace staging and promotion semantics;
- clarification and approval models;
- code-generation-specific projections and UI.

Siming must not reference Guyabano or any of its primitives.

Storage providers implement the core `IAppendOnlyLedger` contract. The core API
must not expose SQLite connections, SQL syntax, transaction types, file paths, or
provider-specific options. Provider construction and configuration remain in the
provider package. Every provider must pass `Penghou.Siming.Testing` conformance;
SQLite is the first implementation, not a privileged architecture dependency.

## Testing strategy

Create a provider-neutral conformance suite in the core test package and execute
it against the in-memory and SQLite implementations.

Required categories:

- basic append, multiple streams, ordered reads, pagination and reopen;
- golden encoding and canonical serialization;
- every cryptographically committed field mutation;
- missing, inserted, reordered and truncated rows;
- matching, stale, foreign and rewritten checkpoints;
- update/delete trigger enforcement;
- transaction rollback and cancellation;
- thread and process concurrency;
- WAL/crash recovery and interrupted initialization;
- culture, property-order and CLR-type independence;
- persistence version compatibility.

Full verification may remain O(n) for v1. Do not add Merkle structures until
measurements demonstrate a real need.

## Deferred features

- Merkle accumulators, inclusion proofs and consistency proofs.
- Distributed replication, networking and consensus.
- Blockchain semantics or immudb compatibility.
- Independent per-stream chains.
- Remote checkpoint services.
- TPM or OS-backed signing-key management.
- Arbitrary historical event migration or rewriting.

## Open decisions

1. Application schema identity/version in a future envelope.
2. Exact public ledger-context schema and lifecycle.
3. Whether keyed hashing belongs in core or the cryptography package.
4. Key rotation semantics: new ledger epoch, suite transition event, or both.
5. Supported target frameworks for the first preview.
6. First independent Guyabano checkpoint location: Git trailer, Git note,
   separate file, or multiple anchors.
7. Guyabano payload classification and retention policy for prompts, model
   responses, tool output, and generated content.

## Current repository state

- The repository contains the core, SQLite, cryptography, testing, verifier CLI,
  and test projects. Public APIs remain pre-release.
- The core supports raw definitive bytes and typed append requests through an
  injected serializer. Canonical JSON, the v1 binary encoder/hasher, an in-memory
  provider, checkpoint-aware verification, and the first golden vector are
  implemented.
- The SQLite provider has immutable metadata/entry tables, WAL setup,
  immediate transactional appends, ordered and paged reads, stream filtering,
  update/delete rejection triggers, reopen verification, and separate-instance
  concurrency coverage. It also has explicit compatibility errors, injected
  rollback/cancellation tests at each append boundary, killed-process WAL
  recovery, true multi-process contention, deterministic busy-timeout behavior,
  strict read-only operation, stable verification snapshots, and schema-object
  compatibility coverage.
- The full suite passes 51 tests (26 core and 25 SQLite), and the independent
  Python verifier reproduces the v1 golden hash.
- Canonical JSON is cross-checked against portable Guyabano `v2` compatibility
  vectors, including exponent, negative-zero, escaping, and property ordering.
- Provider-neutral append limits cover payload and UTF-8 metadata sizes before
  mutation; custom limits are tested in both in-memory and SQLite providers.
- Direct idempotency lookup is provider-neutral and covered by conformance and
  reopen tests. SQLite disposal drains its matching pool so independent ledger
  files can be moved or archived, and multi-ledger isolation is tested.
- SQLite uses Microsoft.Data.Sqlite 10.0.11, and CI restores and audits an
  isolated project from uniquely versioned packed artifacts.
- Portable and signed checkpoints, bounded verification, progress,
  cancellation, and the operational verifier CLI are implemented.
- The repository is committed and connected to its GitHub remote.
