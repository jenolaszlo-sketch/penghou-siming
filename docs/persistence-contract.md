# Persistence contract

This document records the implemented v1 core and SQLite persistence boundary.

## Authority

The ledger is one global ordered sequence. `StreamId` partitions application
events for reading, but a stream-only read is not independently verifiable:
verification follows the global chain or a trusted global checkpoint.

## Hash input

Each row hash commits to a versioned, unambiguous binary envelope containing:

1. format version;
2. immutable ledger ID;
3. global sequence;
4. UTF-8 stream ID with byte length;
5. ledger-assigned UTC commit time as Unix epoch milliseconds;
6. UTF-8 event type with byte length;
7. UTF-8 content type with byte length;
8. UTF-8 serialization format with byte length;
9. serialization version as a 32-bit integer;
10. definitive payload byte length and exact stored bytes;
11. optional UTF-8 idempotency key, encoded with length `-1` when absent;
12. the previous 32-byte SHA-256 hash.

Integers use fixed-width big-endian encoding. Strings are UTF-8. Variable fields
are byte-length-prefixed. The first row references a deterministic, documented
genesis hash.

The v1 genesis hash is `SHA256(UTF8("penghou-siming-ledger-v1"))`.

The first committed golden vector is encoded by
`LedgerFormatV1Tests.GoldenVector_IsStableAndIndependentlyReproducible`. Its row
hash is:

```text
f31470fc756cc7e09a8f87eeb93643f4586f572487d18524adafe88b6318c9e9
```

## SQLite responsibilities

The SQLite backend atomically reads the head, allocates the next sequence,
calculate the hash from the bytes being persisted, insert the row, and commit.
Failed or cancelled transactions expose no partial logical event. Triggers reject
ordinary updates and deletes.

The v1 suite is unkeyed SHA-256 and has no external-context field. Future public
context binding or keyed suites require a new format or ledger epoch and never
reinterpret v1 rows. A keyed suite stores only its algorithm identity and key
identifier; secret material remains external.

## Verification limits

Local verification detects malformed rows and changes that were not followed by
recalculation of the chain. A database owner can rewrite the complete chain or
restore an older valid database. Verification against an independently retained
checkpoint detects that replacement or rollback.

Hash chaining proves historical consistency relative to a trusted head. It does
not prove actor identity, timestamp accuracy, payload truth, or authorization.

## Cryptographic boundaries

- Public ledger context (application, environment, tenant, deployment, or
  similar) is correlation data, not secrecy. Committing its digest binds rows to
  that context; it does not conceal the context and must never be used as a
  secret.
- A keyed suite such as `hmac-sha256-v1` authenticates rows with an externally
  held secret. It proves integrity and authenticity to parties that share the
  secret; it is not payload encryption and does not hide event contents.
- Neither context binding nor a keyed suite replaces an independently retained
  checkpoint. A holder of the secret, or an owner able to recompute an unkeyed
  chain, can still rewrite or roll back the entire ledger; only a detached
  checkpoint retained elsewhere detects that replacement or rollback.
- Key material stays external to the ledger: only the suite identity and key
  identifier are persisted, so rotation, availability, backup, and recovery are
  operational responsibilities of the host.

## Planned epoch changes

Context binding and optional keyed suites require a new format/ledger epoch and
never reinterpret committed v1 rows.

- **Public ledger-context binding.** A future epoch commits a canonical digest
  of external identity into the genesis hash, every row hash, and portable
  checkpoints. The digest is taken over a versioned, unambiguous canonical
  encoding; the same context must be supplied to append and to verify, and
  changing context begins a new ledger epoch.
- **Optional keyed suite `hmac-sha256-v1`.** A future suite authenticates rows
  with an external secret while persisting only the suite identity and key
  identifier. A lost or rotated key must never silently fall back to unkeyed
  verification; unavailable key material is reported as a verification failure.
- Independent golden vectors must be published for every context-bound or keyed
  suite before it is declared supported.
- **Envelope schema identity decision.** The ledger envelope does not currently
  commit an application schema identity; the canonical JSON payload carries its
  own contract name and version. If an application schema identity is added, it
  is added only as part of the context-binding epoch above rather than as a
  separate envelope field.

## Evolution

The format version is part of every row hash. An incompatible encoding, hash,
timestamp, or canonicalization change introduces a new version or ledger epoch;
committed historical rows are never reinterpreted or migrated in place.
