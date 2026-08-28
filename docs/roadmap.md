# Roadmap

## Milestone 0 — Contract foundation

- [x] Create the core, SQLite, and test project structure.
- [x] Define initial byte-oriented ledger, entry, head, checkpoint, and
  verification contracts.
- [x] Freeze the initial v1 binary hash-input specification and deterministic
  genesis in code and documentation.
- [ ] Decide whether canonical JSON follows RFC 8785 exactly or a documented
  Penghou canonical JSON contract shared with Guyabano.
- [x] Add the first deterministic golden vector; add an independent non-.NET
  reproduction before declaring Milestone 0 complete.

## Milestone 1 — Core cryptographic format

- [x] Implement immutable hash and identifier value types.
- [x] Implement big-endian, fixed-width and length-delimited v1 encoding.
- [x] Include ledger identity, format version, sequence, stream, committed time,
  event type, payload, and previous hash in every row hash.
- [x] Implement canonical JSON serialization and pre-canonicalized byte input.
- [x] Implement full-chain and checkpoint-aware verification diagnostics.
- [ ] Test malformed encoding, mutation, deletion, insertion, reordering,
  truncation, replacement chains, and unsupported versions.
- [x] Define the provider-neutral ledger/read contracts and a reusable
  `Penghou.Siming.Testing` conformance suite; SQLite and future providers share
  the same behavioral contract.
- [x] Add cryptographically committed, atomically enforced provider-neutral
  idempotency with replay and conflict conformance tests.

## Milestone 2 — SQLite backend

- [x] Add `Microsoft.Data.Sqlite` and create immutable metadata and ledger tables.
- [x] Add explicit missing-column and unsupported-format compatibility
  diagnostics beyond normal SQLite constraint/query failures.
- [x] Implement atomic append using an immediate SQLite write transaction.
- [x] Prove concurrent callers across separate provider instances cannot allocate
  the same sequence or previous head.
- [x] Add `UPDATE` and `DELETE` rejection triggers for entries and metadata.
- [x] Implement ordered and paged reads plus stream filtering.
- [x] Test rollback at every append boundary, cancellation after insert, reopen,
  killed-process WAL recovery, and multi-process writer contention.
- [x] Add deterministic busy-timeout coverage and broaden schema type/constraint
  compatibility inspection.

## Milestone 3 — Checkpoints and operational verification

- [x] Export portable, versioned JSON checkpoints.
- [x] Verify exact heads and valid extensions of earlier checkpoints.
- [x] Detect ledger identity mismatch and rollback against a newer checkpoint.
- [ ] Add bounded verification progress and cancellation.
- [ ] Design optional Ed25519 signed checkpoints without coupling keys to the
  core persistence package.
- [ ] Prototype a standalone verifier CLI after the library contract stabilizes.

## Milestone 4 — Guyabano adoption

- [ ] Add `Guyabano.Session.Sqlite` as the domain adapter; keep coding-specific
  event types out of Siming.
- [ ] Migrate append-only session events from JSONL without rewriting their
  historical meaning.
- [ ] Keep current state, pending input, workspace, approval, timeline, and
  reconciliation tables as rebuildable projections.
- [ ] Anchor selected ledger checkpoints to generated Git commits or another
  independently retained location.
- [ ] Add end-to-end crash, retry, reconciliation, and projection-rebuild tests.

## Deferred

- Merkle accumulators and inclusion proofs.
- Replication, networking, consensus, or blockchain semantics.
- Remote checkpoint services and hardware-backed signing.
- Independent per-stream chains unless a demonstrated use case justifies the
  additional format and concurrency complexity.
