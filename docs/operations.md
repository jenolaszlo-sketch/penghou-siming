# Operations guide

How to run Penghou.Siming ledgers: threats, retention, backup, keys, and
checkpoints. Cryptography contracts live in
[persistence-contract](persistence-contract.md).

## Threat model

Verification proves historical consistency relative to a trusted head: every
row links to its predecessor and recomputes to its stored hash under the
ledger's epoch (unkeyed SHA-256, context-bound SHA-256, or keyed HMAC-SHA256).
It does not prove actor identity, timestamp accuracy, payload truth, or
authorization. Those are application claims carried inside event payloads.

Assume these adversaries:

- **Database owner.** Anyone who can replace the whole database file can
  recompute a valid replacement chain (epochs 1 and 2) or, holding the secret,
  a keyed chain (epoch 3). Detection requires comparing against a checkpoint
  retained elsewhere. This is why checkpoints exist.
- **Rollback.** Restoring an older valid database verifies cleanly on its own.
  Only a newer trusted checkpoint (higher sequence) detects rollback.
- **Key holder (epoch 3).** Whoever holds the HMAC secret can author history.
  Key compromise equals ledger authorship. Rotate by key identifier and epoch
  (below); never reuse a compromised key identifier.
- **Signature key holder.** Whoever holds an Ed25519 signing key can mint
  checkpoints. A signed checkpoint authenticates the document, not the ledger:
  it must still anchor a verification run to mean anything.
- **Truncation without a checkpoint.** Dropping unanchored tail rows is
  undetectable. Anchor the head you care about before it matters.

What Siming does not defend against: payload secrecy (hashes and HMACs hide
nothing; encrypt before appending if contents are sensitive), network
attackers (there is no network surface), or host compromise (a compromised
host leaks secrets and checkpoints alike).

## Retention

Ledgers are append-only by design: no update or delete API exists, and SQLite
triggers reject `UPDATE`/`DELETE`. Plan retention up front:

- Bound payloads (`LedgerInputLimits`, default 16 MiB) and prefer content
  identities over raw bulk.
- Retire ledgers by age or size: capture and anchor a final checkpoint, archive
  the database file, and start a new ledger (a new epoch if context or keys
  changed). Link generations through checkpoint records in your own index; the
  ledger never rewrites history to forget it.
- `VACUUM` cannot shrink a ledger with live rows; it only compacts freelist
  pages. Do not expect deletion-driven space recovery.

## Backup

Back up the database file plus operational metadata (ledger reference, epoch,
context values, key identifiers — never secrets in the same place):

- SQLite: with pooling disabled and no writer active, copy the `.db` file. If
  WAL mode left `-wal`/`-shm` sidecars, either checkpoint them first
  (`PRAGMA wal_checkpoint(TRUNCATE)`) or copy all three files atomically. The
  provider's dispose-with-pooling-cleared path exists for archival moves.
- Secrets (HMAC keys, exportable signing keys) back up separately under the
  host's key management, with access controls and rotation history.
- Test restores: open the copy read-only and run full verification
  (`penghou-siming-verify <copy>` with the epoch's context/key flags) plus a
  checkpoint comparison before trusting it.

## Signing keys (Ed25519)

- Generate with `Ed25519CheckpointSigner.Generate`, which is non-exportable by
  default. Pass an explicit opt-in only for keys you intend to back up or move.
- Store private keys in the OS key store (DPAPI, Keychain, libsecret) or an
  HSM; implement `ILedgerCheckpointSigner`/`ILedgerCheckpointVerifier` for
  remote signers rather than importing raw keys across machines.
- Rotate by key identifier: new signatures use the new ID, old signatures keep
  verifying under the old public key. Never reuse an identifier after
  compromise.
- Publish the fingerprint (`Ed25519CheckpointVerifier.Fingerprint`, reported by
  the CLI) out of band so verifiers can confirm they trust the right key.

## HMAC suite keys (epoch 3)

- Generate 32 cryptographically random bytes (`RandomNumberGenerator`) per key
  identifier. Store as a raw 32-byte file with owner-only permissions or in a
  secret manager; pass it to the CLI with `--hmac-key-file`, never on a
  command line.
- Rotation starts a new ledger epoch under a new key identifier. Retain retired
  keys for verifying retired ledgers; a lost key makes its history permanently
  unverifiable.
- Clear in-memory copies when done (`LedgerHmacKey.Clear()`; the CLI clears
  after each run).

## Checkpoints

- Capture after meaningful milestones (batch close, deployment, approval), not
  after every row. Export the portable document and anchor it independently:
  a separate file, a signed envelope, or a Git trailer/note (see the P3
  anchoring sample).
- Signed checkpoints need `--public-key` plus the expected `--key-id`, and the
  checkpoint's epoch (context, and suite/key for epoch 3) must be supplied at
  verification. A checkpoint from another ledger, epoch, context, or key fails
  closed with identity, version, or hash diagnostics — investigate those as
  potential tampering, never as noise to retry past.
- Verify on a schedule, not just on suspicion: `VerifyAsync` with the anchored
  checkpoint, bounded page sizes, and cancellation for large ledgers.
