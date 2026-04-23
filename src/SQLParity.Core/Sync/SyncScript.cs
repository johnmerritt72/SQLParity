using System;

namespace SQLParity.Core.Sync;

public sealed class SyncScript
{
    public required string SqlText { get; init; }
    public required DateTime GeneratedAtUtc { get; init; }
    public required string DestinationDatabase { get; init; }
    public required string DestinationServer { get; init; }
    public required int TotalChanges { get; init; }
    public required int DestructiveChanges { get; init; }
}
