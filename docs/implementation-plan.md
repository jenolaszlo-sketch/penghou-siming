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

The implementation should use RFC 8785 if it can satisfy the required .NET
compatibility and existing Guyabano semantics. Otherwise, publish an exact
Penghou canonical JSON contract covering:

- ordinal property ordering;
- preserved array order;
- no insignificant whitespace;
- deterministic UTF-8 and string escaping;
- deterministic number representation;
- explicit null, boolean, string, array, and object handling.

Before Guyabano deletes its local canonicalizer, Siming must publish golden
vectors proving compatibility with Guyabano artifact hash contract `v2`.
Existing Guyabano hashes must never be reinterpreted.

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

Checkpoint storage, Git anchoring, signatures, and key management remain outside
the core persistence responsibility. Optional Ed25519 signed-checkpoint contracts
may be added later without making signatures mandatory.

## Preferred implementation sequence

### Milestone 0 — Freeze the persistence contract

Do this before creating durable SQLite data.

- [ ] Finalize the v1 binary envelope field list and encoding.
- [ ] Decide RFC 8785 versus a documented Penghou canonical JSON contract.
- [ ] Freeze ledger ID, format version, genesis, timestamp, and serializer
  descriptor semantics.
- [ ] Define exact-head and valid-extension checkpoint algorithms.
- [ ] Publish golden binary/hash vectors independent of SQLite and CLR types.
- [ ] Document forward-compatibility and unsupported-version behavior.

Exit criterion: an independent implementation can reproduce every v1 hash.

### Milestone 1 — Core cryptographic engine

Implement in `Penghou.Siming`:

- [ ] immutable and allocation-safe `LedgerHash` and `LedgerId` value types;
- [ ] raw payload and typed append request contracts;
- [ ] `ILedgerPayloadSerializer` and `SerializedLedgerPayload`;
- [ ] canonical JSON serializer and golden-vector tests;
- [ ] v1 binary envelope encoder and SHA-256 row hasher;
- [ ] detailed full-chain and checkpoint verification;
- [ ] an in-memory ledger primarily for provider conformance tests.

Adversarial tests must cover payload, event type, timestamp, stream, previous
hash, row hash, sequence, ledger ID and serializer descriptor mutation; deletion,
insertion, reordering and truncation; rewritten suffixes; culture changes;
equivalent JSON; invalid encoding; and unsupported versions.

Exit criterion: cryptographic semantics are proven without SQLite.

### Milestone 2 — SQLite append and query path

Implement in `Penghou.Siming.Sqlite`:

- [ ] add `Microsoft.Data.Sqlite`;
- [ ] immutable one-row metadata table and ledger-entry table;
- [x] schema initialization and compatibility validation;
- [ ] atomic append using a SQLite write transaction such as `BEGIN IMMEDIATE`;
- [ ] transactionally allocate the next sequence and read the previous head;
- [ ] hash the exact bytes inserted into the database;
- [ ] `UPDATE` and `DELETE` rejection triggers;
- [ ] ordered/paged reads and stream filtering;
- [x] reopen, empty-head, rollback, cancellation, and schema tests.

Exit criterion: a single process can safely create, append, reopen, page, and
verify a durable ledger.

### Milestone 3 — Concurrency and crash recovery

- [ ] concurrent callers never allocate the same sequence or previous head;
- [ ] multiple processes serialize commits correctly;
- [x] busy timeout and cancellation behavior is deterministic;
- [ ] WAL recovery exposes no partial logical entry;
- [ ] interrupted initialization is recoverable;
- [ ] direct SQL corruption produces precise verification failures;
- [ ] tail truncation and older database restoration fail against checkpoints;
- [ ] add reusable backend conformance tests.

Exit criterion: Siming remains valid under realistic embedded failure and
contention modes. Guyabano adoption should not start before this milestone.

### Milestone 4 — Checkpoints and operations

- [ ] portable deterministic checkpoint serialization;
- [ ] exact-head and valid-extension verification;
- [ ] optional signed-checkpoint contracts without core key management;
- [ ] bounded verification progress and cancellation;
- [ ] Git trailer/note anchoring sample or adapter;
- [ ] consider a small independent verifier CLI after contracts stabilize.

Exit criterion: a ledger can be checked against independently retained trust
state and used operationally without application-specific code.

### Milestone 5 — Package readiness

- [ ] public API baselines and compatibility policy;
- [ ] XML documentation and examples;
- [ ] trimming and Native AOT review;
- [ ] CI for supported .NET targets and operating systems;
- [ ] package metadata, license, source link, deterministic builds and symbols;
- [ ] append, pagination and full-verification benchmarks;
- [ ] threat model, payload retention, backup and checkpoint guidance;
- [ ] publish preview packages.

Exit criterion: Siming is independently usable rather than merely extracted
Guyabano code.

### Milestone 6 — Guyabano dogfooding and reduction

Create `Guyabano.Session.Sqlite` as the domain adapter.

- [ ] map Guyabano `SessionEvent` to Siming streams and payloads;
- [ ] preserve event IDs, explicit sequence semantics where needed,
  idempotency keys, causation, correlation and cross-system references;
- [ ] import JSONL history into a new ledger without pretending the imported
  rows were originally committed by Siming;
- [ ] append a migration/import provenance event and retain source identity;
- [ ] replace the filesystem session event store;
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

Resolve these during Milestone 0:

1. Does v1 implement RFC 8785 exactly, or preserve Guyabano's current canonical
   JSON v2 rules and name/version that contract explicitly?
2. Are `ContentType`, `SerializationFormat`, and `SerializationVersion`
   sufficient, or does the row also commit optional application `Schema` and
   `SchemaVersion` fields?
3. Should `LedgerHash` equality be fixed-time everywhere or only in verification
   and checkpoint comparison paths?
4. What deterministic checkpoint serialization format should be portable across
   languages?
5. Which .NET target frameworks ship in the first preview: .NET 8 only, or .NET
   8 and .NET 10 like other Penghou packages?
6. Where will Guyabano retain its first independent trusted checkpoint: Git
   trailer, Git note, separate file, or more than one location?
7. What payload classification and retention policy must Guyabano enforce before
   prompts or model outputs may be committed immutably?

## Current repository state

- The repository, solution, core package, SQLite package and two test projects
  are scaffolded.
- Initial ledger contracts and SQLite options exist but are explicitly
  pre-release and not frozen.
- The core supports raw definitive bytes and typed append requests through an
  injected serializer. Canonical JSON, the v1 binary encoder/hasher, an in-memory
  provider, checkpoint-aware verification, and the first golden vector are
  implemented.
- The first SQLite provider now has immutable metadata/entry tables, WAL setup,
  immediate transactional appends, ordered and paged reads, stream filtering,
  update/delete rejection triggers, reopen verification, and separate-instance
  concurrency coverage. It also has explicit compatibility errors, injected
  rollback/cancellation tests at each append boundary, killed-process WAL
  recovery, true multi-process contention, deterministic busy-timeout behavior,
  and declared type/nullability/key schema compatibility coverage.
- The full suite passes 36 tests (21 core and 15 SQLite), and the independent
  Python verifier reproduces the v1 golden hash.
- The repository is initialized locally but has not yet been committed or
  connected to the GitHub remote.
