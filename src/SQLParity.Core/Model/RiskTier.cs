namespace SQLParity.Core.Model;

/// <summary>
/// Risk classification for a change. Drives UI presentation and the
/// destructive-change gauntlet.
/// </summary>
public enum RiskTier
{
    /// <summary>No data loss possible.</summary>
    Safe,

    /// <summary>No direct data loss, but operational risk (locks, validation failures).</summary>
    Caution,

    /// <summary>Data modification but not outright loss.</summary>
    Risky,

    /// <summary>Data loss is certain or very likely, or an existing object is being removed.</summary>
    Destructive
}
