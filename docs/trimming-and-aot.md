# Trimming, Native AOT, and target frameworks

Review status as of `0.1.0-preview.6`. Reproduce with a .NET 8 SDK on Windows:

```powershell
dotnet publish src/Penghou.Siming.Verify/Penghou.Siming.Verify.csproj -c Release `
  -p:PublishTrimmed=true --self-contained -r win-x64 -o artifacts/trimcheck
```

## Target frameworks: net8.0 only

All packable projects target `net8.0` only. Rationale:

- Siming is a small embedded library with no target-dependent code; one TFM
  keeps a single public-API baseline and test matrix.
- net8.0 remains the broadest consumer base for an embeddable ledger.
- Revisit when net8.0 approaches end of life or when a consumer needs a newer
  TFM-only runtime feature.

## Trimming: clean

A trimmed self-contained publish of the Verify tool reports zero `IL20xx`
warnings. The only findings during the review were generic JSON serialization
of caller payload types, resolved as follows:

- `ILedgerPayloadSerializer.Serialize<T>` and both implementations carry
  `[RequiresUnreferencedCode]`; the contract requires it on the interface so
  annotations match across implementations (IL2046).
- New `Serialize<T>(T, JsonTypeInfo<T>)` overloads give trimmed and AOT
  applications a fully static serialization path via source generation.
- Typed `AppendAsync<T>` (contract and both providers) carries
  `[RequiresUnreferencedCode]`; byte-oriented
  `AppendAsync(LedgerAppendRequest)` is the trim-safe append path.
- The Verify tool emits all JSON through a source-generated
  `JsonSerializerContext`; no reflection-based serialization remains in it.
- NSec and Microsoft.Data.Sqlite produce no trim warnings in this closure.

## Native AOT: analysis clean, native link not verified here

The AOT analyzer reports no `IL30xx` warnings for the Verify tool closure
after the annotations above (`RequiresDynamicCode` accompanies each
`RequiresUnreferencedCode` on the generic serialization APIs). A full native
link was not produced on the review machine (no Windows SDK linker
installed). To prove it in CI or locally with a toolchain:

```powershell
dotnet publish src/Penghou.Siming.Verify/Penghou.Siming.Verify.csproj -c Release `
  -p:PublishAot=true --self-contained -r win-x64 -o artifacts/aotcheck
```

On Linux the same command needs `clang` and `zlib1g-dev`. Callers AOT-compiling
their own hosts must use the `JsonTypeInfo<T>` serializer overloads or
pre-serialized byte payloads; the generic `Serialize<T>`/`AppendAsync<T>` APIs
require dynamic code by contract.
