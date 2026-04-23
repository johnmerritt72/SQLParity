using System.Collections.Generic;
using System.Linq;

namespace SQLParity.Core.Model;

/// <summary>
/// The result of comparing two database schemas. Contains all detected
/// changes, classified by risk tier.
/// </summary>
public sealed class ComparisonResult
{
    public required DatabaseSchema SideA { get; init; }
    public required DatabaseSchema SideB { get; init; }
    public required IReadOnlyList<Change> Changes { get; init; }

    public int SafeCount => Changes.Count(c => c.Risk == RiskTier.Safe);
    public int CautionCount => Changes.Count(c => c.Risk == RiskTier.Caution);
    public int RiskyCount => Changes.Count(c => c.Risk == RiskTier.Risky);
    public int DestructiveCount => Changes.Count(c => c.Risk == RiskTier.Destructive);
    public int TotalCount => Changes.Count;
}
