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

## Epoch 2: context-bound rows

A ledger context (`LedgerContext`) binds external identity into a new ledger
epoch (format v2). At least one of application, environment, tenant, or
deployment must be set; each field is bounded to 256 UTF-8 bytes.

The canonical context digest is `SHA256` over:

1. the domain string `penghou-siming-ledger-context-v1` with a NUL terminator;
2. for each identity field in fixed order (application, environment, tenant,
   deployment): a presence byte, then the UTF-8 byte length and exact bytes
   when present.

The v2 genesis is `SHA256("penghou-siming-ledger-v2" + NUL + context-digest)`.
Each v2 row hash commits the exact v1 envelope field order with the format
version `2`, followed by the 32-byte context digest. The committed v2 golden
vector is encoded by `LedgerContextTests.V2_RowHash_IsStableAndBoundToContext`:

```text
f8c7fc5beac434819cb15a90a19adfb5d622c06d4162fff17841dddc0ddc1257
```

Epoch rules:

- v1 rows are never reinterpreted; a v1 ledger verifies only with a null
  context, and a context-bound ledger verifies only with its exact context.
- A checkpoint records its epoch in `FormatVersion`; mismatched epochs report
  `UnsupportedVersion` rather than a hash failure.
- Portable checkpoints for epoch 2 require the context at import; a context
  presented for an epoch-1 checkpoint, or no context for an epoch-2
  checkpoint, is rejected.
- SQLite databases record their epoch in `ledger_metadata.format_version`.
  Opening with a mismatched context fails closed; changing context begins a
  new ledger in a new database. The table schema is unchanged.
- Signed checkpoints authenticate the document bytes; the ledger context binds
  when the checkpoint anchors verification, not at signature time.

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

## Epoch 3: keyed suite

Format v3 (`hmac-sha256-v1`) streams the epoch-2 row envelope through
HMAC-SHA256 under an external 32-byte secret. The suite identity, key
identifier, and context digest are bound alongside every field; the v3 genesis
binds the same values. The committed v3 golden vector lives in
`vectors/ledger-format-v3.json` and is recomputed independently by
`tools/verify_golden_vectors.py`:

```text
bc442eddccd5c06583c08a5d318056b580cbf3eb81627676fe4dcbb118b1aee8
```

Key rules:

- The secret is exactly 32 bytes, held externally, and never persisted by
  Siming. Only the suite identity (as format version 3) and the key identifier
  (in checkpoint envelopes and diagnostics) are retained.
- Verification requires the exact secret, context, and key identifier. A wrong
  secret fails as a hash mismatch; a wrong key identifier fails as an identity
  mismatch against the checkpoint binding. A key without a context is
  rejected; unkeyed epochs never accept keyed material.
- Rotation assigns a new key identifier and begins a new ledger epoch. The old
  key must be retained to verify old history; a lost key makes that history
  unverifiable without ever silently falling back to unkeyed verification.
- Backup covers the secret separately from the database under the host's key
  management. Recovery restores both; verification availability depends on
  both.

## Planned epoch changes

Further suites require a new format/ledger epoch and
never reinterpret committed rows.

- **Public ledger-context binding (implemented as epoch 2).** See
  "Epoch 2: context-bound rows" above.
- **Keyed suite `hmac-sha256-v1` (implemented as epoch 3).** See
  "Epoch 3: keyed suite" above.
- Independent golden vectors must be published for every keyed
  suite before it is declared supported. Epoch-2 and epoch-3 vectors are
  published in `vectors/` and verified independently.
- **Envelope schema identity decision.** The ledger envelope does not currently
  commit an application schema identity; the canonical JSON payload carries its
  own contract name and version. If an application schema identity is added, it
  is added only as part of the context-binding epoch above rather than as a
  separate envelope field.

## Evolution

The format version is part of every row hash. An incompatible encoding, hash,
timestamp, or canonicalization change introduces a new version or ledger epoch;
committed historical rows are never reinterpreted or migrated in place.
