# Penghou.Siming Benchmarks

The reproducible BenchmarkDotNet harness lives in
`benchmarks/Penghou.Siming.Benchmarks`. It exercises in-memory and SQLite
appends, paginated reads, full verification, and large (1 MiB) payloads.

Run the matrix in Release mode:

```powershell
dotnet run -c Release --project benchmarks/Penghou.Siming.Benchmarks -- --job short
```

## Baseline (2026-09-26)

Measured on 2026-09-26 using BenchmarkDotNet 0.15.8, .NET 8, Windows 11, and
an Intel Core Ultra 5 125H, with a short smoke job (`--job short`; SQLite
operations show high variance across the 3 iterations). Results are local
engineering baselines, not portable performance guarantees. `EntryCount` is
the pre-filled ledger size; each append benchmark adds one small row.
`AppendLargePayload` appends one 1 MiB row to a fresh ledger and
`VerifyLargePayload` verifies 10 pre-filled 1 MiB rows, so both are
independent of `EntryCount`.

| Operation | 100 entries | 1,000 entries |
|---|---:|---:|
| Append (memory) | 3.68 µs | 3.58 µs |
| Append (SQLite) | 5.09 ms | 4.94 ms |
| Read page of 100 (memory) | 2.81 µs | 6.68 µs |
| Verify all (memory) | 178 µs | 1.73 ms |
| Verify all (SQLite) | 3.03 ms | 7.71 ms |
| Append 1 MiB payload (memory) | 1.05 ms | 1.10 ms |
| Verify 10 x 1 MiB payloads (memory) | 4.69 ms | 4.76 ms |

Reads: in-memory appends are flat versus ledger size; SQLite appends are
dominated by transaction commit cost. Verification scales linearly with entry
count on both providers. Large-payload cost is dominated by the single
incremental hash pass with no payload copy.

Benchmark output belongs under `BenchmarkDotNet.Artifacts`, which is ignored
by Git. Re-run the matrix when changing the hash envelope, batching strategy,
or query shape.
