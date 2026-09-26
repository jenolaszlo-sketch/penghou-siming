# Roadmap

This file contains only unfinished work. Implemented behavior and accepted
decisions live in the [implementation plan](implementation-plan.md) and
[persistence contract](persistence-contract.md).

## Priority 1 — Harden verification and trust boundaries

- [x] Unify synchronous and asynchronous verification behind one incremental
  state machine with precise head/snapshot failure diagnostics (`HeadMismatch`
  plus a shared snapshot/head target overload).
- [x] Bound checkpoint and signing-key inputs; hash large envelopes
  incrementally rather than copying the entire payload (checkpoint/envelope
  size limits, Ed25519 key-size and key-ID bounds, `IncrementalHash` row
  hashing that preserves the v1 byte sequence).
- [x] Make generated signing keys non-exportable by default and define extension
  points for OS, HSM, and remote signers (`ILedgerCheckpointSigner` /
  `ILedgerCheckpointVerifier`; export requires explicit opt-in).
- [x] Return machine-readable CLI input errors and the verified key fingerprint.

## Priority 2 — Complete and evolve the cryptographic contract

- [x] Add the separately versioned Penghou canonical JSON v2 serializer with
  duplicate-property rejection, source-token number normalization, bounded
  exponent/output work, and adversarial/golden coverage. Preserve v1 without
  reinterpretation; downstream logical-content adapters must wait for the v2
  package release.

- [x] Complete adversarial coverage for every committed field, malformed
  encoding, insertion, deletion, reordering, truncation, replacement chains,
  culture, and unsupported versions (v2 now rejects non-UTF-8 persisted JSON
  explicitly with `JsonException`; culture independence and version/insertion
  cases covered).
- [ ] Add immutable public ledger-context binding in a future format/ledger
  epoch. Commit a canonical digest of application, environment, tenant,
  deployment, or similar external identity. (Epoch design recorded in
  [persistence-contract.md](persistence-contract.md).)
- [ ] Design an optional keyed suite such as `hmac-sha256-v1`. Keep the secret
  external; persist only suite and key ID; define rotation, availability,
  backup, and recovery behavior. (Suite design recorded in
  [persistence-contract.md](persistence-contract.md).)
- [ ] Never retrofit or reinterpret v1. Publish independent golden vectors for
  every context-bound or keyed suite.
- [x] Document that public context is not secrecy, keyed hashing is not payload
  encryption, and neither replaces independently retained checkpoints
  (`persistence-contract.md` "Cryptographic boundaries").
- [x] Decide whether a future envelope commits application schema identity and
  version. Decision: no separate envelope field; a payload carries its own
  contract/version, and an application schema identity would be added only as
  part of the context-binding epoch if ever needed.

## Priority 3 — Package readiness

- [x] Establish public API baselines and compatibility policy (PublicApiAnalyzers
  5.6.0 baselines for every packable project; RS0016/RS0017 enforced as build
  errors).
- [ ] Complete API usage examples beyond the package README.
- [ ] Expand CI beyond Linux and establish package/public-API compatibility
  baselines.
- [ ] Review trimming, Native AOT, and supported target frameworks.
- [ ] Benchmark append, pagination, verification, and large payloads.
- [ ] Publish threat-model, retention, backup, signing-key, and checkpoint
  operational guidance.
- [ ] Add a Git trailer/note checkpoint anchoring sample or adapter.

## Priority 4 — Guyabano adoption

- [ ] Add `Guyabano.Session.Sqlite` as the domain adapter; keep coding-specific
  event types out of Siming.
- [ ] Migrate append-only session events from JSONL without rewriting their
  historical meaning.
- [ ] Keep current state, pending input, workspace, approval, timeline, and
  reconciliation tables as rebuildable projections.
- [ ] Anchor selected ledger checkpoints to generated Git commits or another
  independently retained location.
- [ ] Add end-to-end crash, retry, reconciliation, and projection-rebuild tests.

## Deferred until demonstrated need

- [ ] Add optional Merkle proof support after Guyabano integration and
  verification benchmarks demonstrate a need for selective disclosure or
  sublinear verification. Treat the accumulator as a rebuildable index over
  authoritative ledger entries, not as replacement storage.
  - Specify versioned leaf, node, root, and proof encodings with independent
    golden vectors.
  - Evaluate a Merkle Mountain Range and a Certificate Transparency-style tree
    for append performance, proof size, and implementation simplicity.
  - Provide inclusion proofs for individual entries and consistency proofs that
    a newer root is an append-only extension of an earlier root.
  - Capture Merkle roots in checkpoints and support the existing detached
    signature and external anchoring mechanisms.
  - Define transactional SQLite maintenance, crash recovery, deterministic
    rebuilding, corruption diagnostics, and provider-neutral APIs.
  - Benchmark proof generation, verification, storage overhead, and rebuilds on
    realistic Guyabano ledgers before making the feature part of the stable API.
- Replication, networking, consensus, or blockchain semantics.
- Remote checkpoint services and hardware-backed signing.
- Independent per-stream chains unless a demonstrated use case justifies the
  additional format and concurrency complexity.
