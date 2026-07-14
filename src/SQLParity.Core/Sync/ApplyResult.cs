using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLParity.Core.Sync;

public sealed class ApplyStepResult
{
    public required string ObjectName { get; init; }
    public required string Sql { get; init; }
    public required bool Succeeded { get; init; }
    public required string? ErrorMessage { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Server-side info messages emitted during this step (PRINT, RAISERROR
    /// at severity ≤ 10). Empty when the step produced none. Surfaces things
    /// like "Skipped permissions for [X]" from the permission script's guard.
    /// </summary>
    public IReadOnlyList<string> InfoMessages { get; init; } = Array.Empty<string>();
}

public sealed class ApplyResult
{
    public required DateTime StartedAtUtc { get; init; }
    public required DateTime CompletedAtUtc { get; init; }
    public required string DestinationDatabase { get; init; }
    public required string DestinationServer { get; init; }
    public required IReadOnlyList<ApplyStepResult> Steps { get; init; }
    public required bool FullySucceeded { get; init; }

    public int SucceededCount => Steps.Count(s => s.Succeeded);
    public int FailedCount => Steps.Count(s => !s.Succeeded);
}
