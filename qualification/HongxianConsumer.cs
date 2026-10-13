using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;

var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var session = SessionId.New();
var request = new SessionEventRequest(session, SessionParticipantAttribution.Human("qualification"),
    SessionEventTypes.UserMessage, DateTimeOffset.UtcNow, PayloadJson: "{\"message\":\"hello\"}",
    IdempotencyKey: "message:1");
await using (var writer = new SimingSessionEventStore(root))
{
    var first = await writer.AppendAsync(request);
    var replay = await writer.AppendAsync(request with { OccurredAt = request.OccurredAt.AddMinutes(1) });
    if (first.Sequence != 1 || replay.EventId != first.EventId || replay.Sequence != first.Sequence)
        throw new InvalidOperationException("Hongxian retry contract");
    var page = await writer.ReadPageAsync(new SessionEventPageRequest(session, Limit: 1));
    if (page.Events.Count != 1 || page.Events[0].EventId != first.EventId || page.HasMore)
        throw new InvalidOperationException("Hongxian paging contract");
}
await using (var reader = new SimingSessionEventStore(root))
{
    var events = await reader.ReadAsync(session);
    var verified = await reader.VerifyChainAsync(session);
    if (events.Count != 1 || verified?.Sequence != 1 || events[0].PayloadJson != request.PayloadJson)
        throw new InvalidOperationException("Hongxian reopen verification");
}
Console.WriteLine("SUPPORTED BY THIS PROBE: published Hongxian.Sqlite preview.5 append/retry/page/reopen/verification");
