namespace SQLParity.Core.Model;

/// <summary>
/// A single reason explaining why a change was classified at its risk tier.
/// </summary>
public sealed class RiskReason
{
    public required RiskTier Tier { get; init; }
    public required string Description { get; init; }

    public override string ToString() => $"[{Tier}] {Description}";
}
