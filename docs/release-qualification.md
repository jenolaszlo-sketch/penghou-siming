# Siming release qualification

Release status is evidence-driven. A document or local build does not claim a
published release. The selected five packages share one version, target net8.0,
and retain AssemblyVersion 1.0.0.0. RC1 uses the published preview.7 SDK baseline;
final 1.0 uses RC1. Shipped signatures are reviewed in api-review.json.

The existing trusted publish.yml is dispatched from the clean reviewed main
commit. It packs exactly once, verifies all five packages and symbols against
their version/source commit, retains SHA256s, downloads that identical artifact
set for Windows/Linux/macOS qualification, and publishes only after all gates
pass. No publish job rebuilds. Public packages must have the exact same ZIP
entry names and bytes, excluding only NuGet's repository signature. NuGet
indexing may take up to an hour; unavailable versions are retried, different
payloads fail immediately. Tags are provenance only and follow successful
public qualification, avoiding a second publication.

The standalone net8 probe restores an exact preview or RC baseline from the
public feed into a fresh per-run cache, exercises real append/retry/conflict,
typed serialization, page, checkpoint/signature, reopen, provider conformance
and canonical identity paths, then replaces only product DLLs in the unchanged
consumer output. Its retained preview/RC database is reopened unchanged. The
existing source test harness is compiled, then all product libraries and the
installed verifier payload are replaced with the exact package DLLs before
running its 108 ownership/format/rollback/process-kill/multiprocess/restart tests.

Marang's fingerprint source is copied only into the transient consumer output
from its public checkout at bca73774386e3ae2a455a00db3ff637ea5c0db6e. Its exact
source SHA256 is 21FC4925F98319B83B62B5E445BF44EEFC412219224D678445BC4C2CA3E7F7D8.
It exercises canonical JSON v2 computation, verification and mismatch; the
Marang service and its unrelated dependencies are OUT OF SCOPE.

Guyabano is a public source checkout at
df845087a663b51929c081f7e83b9852598bc995. The qualifier archives its committed
Session, Session.Sqlite and Session.Tests paths into an isolated output tree.
Only package references change. Original session append/retry/projection/cache,
page/reopen/verification tests and Git checkpoint anchoring tests run against
baseline and candidate packages; the same test and adapter binaries then run
again after only Siming DLL replacement, with hashes proving no consumer rebuild.
No source or metadata in the original consumer checkout changes.

Published Hongxian.Sqlite 0.1.0-preview.5 is restored as a package in a net10
consumer with direct Siming overrides. Its unchanged public DLLs exercise
session append/retry/page/reopen/verification, and an unchanged baseline-compiled
consumer reruns against the candidate. This is SUPPORTED BY THIS PROBE for the
selected store path, not Hongxian graduation or certification of every subsystem.

Each qualifier records repository/branch/commit, clean-state evidence, UTC time,
OS/framework, exact versions, artifact or consumer hashes, commands and scope.
TRX and logs are retained alongside JSON. Remote release artifacts are retained
30 days; final summaries and per-platform JSON are committed under docs/evidence
after qualification and link to the immutable release run/source. Stable source
diff must change only version/baseline and release documentation unless another
RC is required for a material product fix.

Stable gate: all shipped members intentional; no unresolved pre-stable question;
all exact RC platform gates; trusted RC publication; matching fresh public bytes;
source, unchanged binary, retained-data and representative consumer proofs;
then final 1.0 exact/public gates and tag. Out-of-scope packages remain preview.
