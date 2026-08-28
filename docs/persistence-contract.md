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

## Evolution

The format version is part of every row hash. An incompatible encoding, hash,
timestamp, or canonicalization change introduces a new version or ledger epoch;
committed historical rows are never reinterpreted or migrated in place.
