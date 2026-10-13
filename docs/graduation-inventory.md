# Siming graduation inventory

Recorded 2026-10-14 before contract changes. Repository `jenolaszlo-sketch/penghou-siming`; original branch `main`; baseline HEAD `af7f15491235443069a61c7eca6f198a02dad41f`. Original checkout clean. Isolated graduation branch `codex/graduation-siming` starts at the same remote main commit.

## Baseline

- Public version: `0.1.0-preview.7`, all five package indexes verified in the preceding assessment.
- Package family: `Penghou.Siming`, `.Sqlite`, `.Cryptography`, `.Testing`, `.Verify` (the last is a dotnet tool, with an intentional embedding entry point).
- Framework: `net8.0`; CI claims Windows, Linux and macOS. No new framework is part of graduation. AOT analysis exists; native link certification is not claimed.
- Public API: core 385, Cryptography 48, SQLite 38, Testing 18, Verify 3 entries in Unshipped; all Shipped files contain only the nullable header. PublicApiAnalyzers 5.6.0 and SDK package validation are enabled; no package baseline version is selected.
- Direct runtime dependencies: core none; SQLite core + Microsoft.Data.Sqlite 10.0.11; Cryptography core + NSec.Cryptography 25.4.0; Testing core; Verify core + SQLite + Cryptography. Analyzer/SourceLink packages are private tooling.
- Consumers: Guyabano.Session.Sqlite .7 session append/read/retry/projection/checkpoint paths; Hongxian.Sqlite .7 per-session ledger provider; Marang.Mcp .7 canonical JSON v2 fingerprints; standalone sample and provider conformance suite. These consumers need not graduate themselves.
- Formats: ledger epochs 1/2/3; SHA-256/context-bound SHA-256/HMAC-SHA256 v1 suite; canonical JSON serializers v1/v2; portable checkpoint document v1; signed checkpoint envelope v1; current SQLite schema and immutable metadata/entry/trigger checks. Existing bytes must not change.
- Existing evidence: 103 tests (67 core, 36 SQLite), independent portable vectors, mutation/ordering/encoding adversarial cases, expected-head/idempotency precedence, rollback/cancellation, killed process before commit, multi-process writers, reopen and read-only snapshots, signer/checkpoint/CLI behavior, runnable Git anchoring sample.
- Existing compatibility: source/API analyzer baseline only; preview policy allows binary breaks and tells consumers to rebuild. Packed consumer script restores/builds/audits without ledger behavior; no unchanged-binary upgrade test.
- CI: build/format/test/sample/vector/pack on three OSes. Publication: `publish.yml` OIDC workflow, tag or manual dispatch, independently repacks then restores a consumer and pushes. It does not yet gate publication on exact cross-platform-qualified artifacts.

## Decisions before implementation

| Question | Classification | Narrow decision |
|---|---|---|
| All current public members | must resolve before stable | Preserve reviewed signatures; they are existing format/provider/serializer/conformance/tool contracts, not speculative placeholders. Record type-level disposition and freeze the exact member baseline. |
| Byte memory ownership and retained state | must resolve before stable | Snapshot append bytes before asynchronous work; detached results must not mutate retained ledger state. Hash/key byte access must not expose internal mutable arrays. DTO record equality is CLR record/ReadOnlyMemory equality, not semantic event equivalence. |
| HMAC clear/dispose | must resolve before stable | Clear is idempotent and terminal. Reading zeroed bytes remains compatible; Validate/hash/provider use after clear throws, never silently hashes with an all-zero secret. Caller retains key ownership and must not dispose during an operation. |
| Verifier embedding | must resolve before stable | Preserve Program.Main/RunAsync deliberately. RunAsync borrows writers, does not dispose them, returns documented exit/result categories; library APIs are preferred for embedding without CLI parsing. |
| Signer behavior | must resolve before stable | Preserve synchronous signer/verifier seam and explicit key ownership; external implementation exceptions propagate. No async/remote signing API added. |
| Retry precedence | must resolve before stable | Equivalent ledger-wide idempotent retry precedes expected-head check. Conflicting content fails without mutation. Cancellation/response loss requires read-by-key/retry reconciliation, never assume no commit from a lost response. |
| Format/wire/schema evolution | must resolve before stable | Keep encodings/versions and current bounded import behavior. Unknown future versions fail; no automatic migration, no reinterpretation. Optional unknown JSON properties remain ignored in current v1 checkpoint parsing; duplicate names must be rejected as ambiguous. |
| General record/domain equality | can explicitly remain unspecified | Record memory/collection equality is ordinary CLR equality; semantic retry equivalence and checkpoint/hash equality are separately specified. Never use record equality as event-content identity. |
| Timing, scheduling, exception text | can explicitly remain unspecified | Stable categories/properties and transactional outcomes, not exact diagnostic prose or wall-clock scheduling. |
| InMemory durability | future work not affecting 1.0 | Ephemeral reference provider; never claim restart persistence. |
| Merkle/networking/remote signer/new formats/frameworks | future work not affecting 1.0 | Deferred, no API scaffolding introduced. |
| Experimental public surfaces | experimental and must leave stable surface | None discovered in the reviewed family. Verifier APIs are intentional; sample/test hooks stay internal. |

## Qualification plan

First reproduce source baseline. Then run source regressions and existing independent vectors, targeted missing ownership/lifetime/JSON ambiguity tests, exact packed behavioral consumers, unchanged preview.7 consumer binaries against RC, unchanged RC binaries against final stable, and representative Guyabano/Hongxian/Marang paths. Build a reviewed exact release commit; retain hashes; qualify the same package set on all supported OSes; publish those retained artifacts via OIDC; repeat public-feed consumer qualification before stable. Label source, local candidate, remote and public evidence separately.
