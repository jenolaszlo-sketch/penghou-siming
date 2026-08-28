using Penghou.Siming;
using Penghou.Siming.Sqlite;

namespace Penghou.Siming.Sqlite.TestHost;

public static class TestHostMarker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
            return 2;
        var mode = args[0];
        var database = args[1];
        var options = new SimingSqliteOptions
        {
            DatabasePath = database,
            Pooling = false,
            BusyTimeout = TimeSpan.FromSeconds(30)
        };
        if (mode == "append")
        {
            var count = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            await using var ledger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(options, new());
            for (var index = 0; index < count; index++)
                await ledger.AppendAsync(new LedgerAppendRequest<int>("child", "number", index));
            return 0;
        }
        if (mode == "crash-after-insert")
        {
            var signal = args[2];
            await using var ledger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
                options,
                new CanonicalJsonPayloadSerializer(),
                async (point, cancellationToken) =>
                {
                    if (point != SqliteAppendFaultPoint.AfterInsertBeforeCommit)
                        return;
                    await File.WriteAllTextAsync(signal, "ready", cancellationToken);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                });
            await ledger.AppendAsync(new LedgerAppendRequest("child", "uncommitted", new byte[] { 1 }));
            return 3;
        }
        return 2;
    }
}
