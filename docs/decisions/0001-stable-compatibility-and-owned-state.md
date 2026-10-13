# ADR 0001 — Stable compatibility and owned state

Status: accepted for 1.0 graduation, 2026-10-14.

The preview policy allowed binary-breaking optional-parameter/record changes and required downstream recompilation. 1.0 supersedes that policy for the selected stable family. All current reviewed public members remain intentional, including independently reproducible format helpers and verifier embedding APIs; no new capability is introduced.

Keep exact public signatures and AssemblyVersion 1.0.0.0 throughout 1.x. Add compatible overloads/body properties or optional capability interfaces; never extend an existing positional constructor or mandatory provider interface. Shipped baselines, a selected package baseline and unchanged compiled consumer probes enforce this. Baseline preview.7 checks migration; RC baseline checks final stable and future 1.x.

Provider-owned state must not alias returned byte buffers. Append snapshots before asynchronous work; hash/key views are detached; ephemeral provider reads/results cannot rewrite retained history. DTOs constructed by callers remain shallow records and CLR equality is not semantic event equivalence. Clear/dispose permanently invalidates key use. Borrowed dependencies have explicit lifetimes; no new ownership abstraction is needed.

Package versions do not change ledger epochs 1/2/3, canonical JSON v1/v2, checkpoint v1 or signature envelope v1. Keep bytes and schema unchanged; incompatible encodings require new named contracts, never reinterpretation. Imports preserve the current unknown-field policy but reject ambiguous duplicate root names before stability.

The alternatives of deleting current proven APIs, creating a new serializer/store abstraction, or silently retaining preview alias/recompilation semantics would add migrations or leave the stable promise indefensible. The normative details and deliberate unspecified behavior live in stable-contract.md. New engines, formats and features remain deferred.
