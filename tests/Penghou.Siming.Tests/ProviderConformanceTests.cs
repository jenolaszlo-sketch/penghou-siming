using Penghou.Siming.Testing;

namespace Penghou.Siming.Tests;

public sealed class ProviderConformanceTests
{
    [Fact]
    public async Task InMemoryProvider_PassesSharedConformanceSuite()
    {
        var result = await LedgerProviderConformance.RunAsync(
            _ => ValueTask.FromResult<IAppendOnlyLedger>(
                new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializer>(new())));

        Assert.True(result.Passed, result.Failure);
        Assert.Equal([
            "empty-head", "global-chain", "conditional-append", "idempotency", "queries",
            "bounded-verification", "verification-cancellation"
        ], result.Checks);
    }
}
