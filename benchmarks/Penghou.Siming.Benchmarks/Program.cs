using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Penghou.Siming;
using Penghou.Siming.Sqlite;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Append, pagination, verification, and large-payload throughput.</summary>
[MemoryDiagnoser]
public class LedgerBenchmarks
{
    private const int LargePayloadBytes = 1024 * 1024;

    private InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializerV2> _memory = null!;
    private SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializerV2> _sqlite = null!;
    private InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializerV2> _large = null!;
    private string _database = null!;
    private byte[] _largePayload = null!;
    private int _counter;

    [Params(100, 1000)]
    public int EntryCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _memory = new(new CanonicalJsonPayloadSerializerV2());
        _database = Path.Combine(
            Path.GetTempPath(), $"siming-benchmark-{Guid.NewGuid():N}.db");
        _sqlite = new(
            new SimingSqliteOptions { DatabasePath = _database, Pooling = false },
            new CanonicalJsonPayloadSerializerV2());
        _largePayload = new byte[LargePayloadBytes];
        Random.Shared.NextBytes(_largePayload);
        _large = new(new CanonicalJsonPayloadSerializerV2());
        for (var index = 0; index < 10; index++)
            await _large.AppendAsync(new LedgerAppendRequest(
                "large", $"chunk-{index}", _largePayload));
        for (var index = 0; index < EntryCount; index++)
        {
            var request = SmallRequest(index);
            await _memory.AppendAsync(request);
            await _sqlite.AppendAsync(request);
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _memory.DisposeAsync();
        await _sqlite.DisposeAsync();
        await _large.DisposeAsync();
        if (File.Exists(_database))
            File.Delete(_database);
    }

    [Benchmark]
    public ValueTask<LedgerEntry> AppendMemory() =>
        _memory.AppendAsync(SmallRequest(Interlocked.Increment(ref _counter)));

    [Benchmark]
    public ValueTask<LedgerEntry> AppendSqlite() =>
        _sqlite.AppendAsync(SmallRequest(Interlocked.Increment(ref _counter)));

    [Benchmark]
    public async Task<int> ReadPage()
    {
        var count = 0;
        await foreach (var entry in _memory.ReadAsync(
                           new LedgerReadRequest(AfterSequence: EntryCount / 2, Limit: 100)))
            count += entry.Payload.Length;
        return count;
    }

    [Benchmark]
    public ValueTask<LedgerVerificationResult> VerifyMemory() =>
        _memory.VerifyAsync();

    [Benchmark]
    public ValueTask<LedgerVerificationResult> VerifySqlite() =>
        _sqlite.VerifyAsync();

    [Benchmark]
    public async Task<LedgerEntry> AppendLargePayload()
    {
        await using var ledger = new InMemoryAppendOnlyLedger<CanonicalJsonPayloadSerializerV2>(
            new(), inputLimits: LedgerInputLimits.Default);
        return await ledger.AppendAsync(
            new LedgerAppendRequest("large", "chunk", _largePayload));
    }

    [Benchmark]
    public ValueTask<LedgerVerificationResult> VerifyLargePayload() =>
        _large.VerifyAsync();

    private static LedgerAppendRequest SmallRequest(int index) => new(
        $"stream-{index % 10}",
        $"event-{index % 5}",
        new byte[] { (byte)(index % 251) });
}
