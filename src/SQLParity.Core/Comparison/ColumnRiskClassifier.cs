using System;
using System.Collections.Generic;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Classifies the risk of a single column-level change.
/// </summary>
public static class ColumnRiskClassifier
{
    public static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) Classify(ColumnChange change)
    {
        var reasons = new List<RiskReason>();

        switch (change.Status)
        {
            case ChangeStatus.New:
                return ClassifyNewColumn(change);

            case ChangeStatus.Dropped:
                reasons.Add(new RiskReason
                {
                    Tier = RiskTier.Destructive,
                    Description = "Data in this column will be lost.",
                });
                return (RiskTier.Destructive, reasons);

            case ChangeStatus.Modified:
                return ClassifyModifiedColumn(change);

            case ChangeStatus.Renamed:
                return (RiskTier.Caution, new[]
                {
                    new RiskReason
                    {
                        Tier = RiskTier.Caution,
                        Description = $"Column renamed from '{change.OldColumnName}' to '{change.ColumnName}'. Data is preserved.",
                    },
                });

            default:
                return (RiskTier.Safe, Array.Empty<RiskReason>());
        }
    }

    private static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) ClassifyNewColumn(ColumnChange change)
    {
        var col = change.SideA ?? change.SideB!;

        if (col.IsNullable)
        {
            return (RiskTier.Safe, new[]
            {
                new RiskReason
                {
                    Tier = RiskTier.Safe,
                    Description = "New nullable column will be added safely.",
                },
            });
        }

        if (col.DefaultConstraint is not null)
        {
            return (RiskTier.Caution, new[]
            {
                new RiskReason
                {
                    Tier = RiskTier.Caution,
                    Description = "New NOT NULL column with default may take a lock on large tables.",
                },
            });
        }

        return (RiskTier.Risky, new[]
        {
            new RiskReason
            {
                Tier = RiskTier.Risky,
                Description = "New NOT NULL column without a default will fail if the table contains rows.",
            },
        });
    }

    private static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) ClassifyModifiedColumn(ColumnChange change)
    {
        var sideA = change.SideA!;
        var sideB = change.SideB!;
        var reasons = new List<RiskReason>();

        // Data type changed
        if (!string.Equals(sideA.DataType, sideB.DataType, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(new RiskReason
            {
                Tier = RiskTier.Risky,
                Description = $"Data type changes from {sideA.DataType} to {sideB.DataType}, may require conversion.",
            });
        }
        else
        {
            // Same type — check maxLength
            if (sideA.MaxLength != sideB.MaxLength && (sideA.MaxLength > 0 || sideB.MaxLength > 0))
            {
                if (sideB.MaxLength > sideA.MaxLength || sideA.MaxLength < 0)
                {
                    // Widened (also covers -1 = MAX as wider than any positive)
                    reasons.Add(new RiskReason
                    {
                        Tier = RiskTier.Caution,
                        Description = $"Column widened from {sideA.MaxLength} to {sideB.MaxLength}.",
                    });
                }
                else
                {
                    // Narrowed
                    reasons.Add(new RiskReason
                    {
                        Tier = RiskTier.Risky,
                        Description = $"Column narrowed from {sideA.MaxLength} to {sideB.MaxLength}, data may be truncated.",
                    });
                }
            }
        }

        // Nullability
        if (!sideA.IsNullable && sideB.IsNullable)
        {
            // NOT NULL → nullable: safe
            reasons.Add(new RiskReason
            {
                Tier = RiskTier.Safe,
                Description = "Column changed from NOT NULL to nullable.",
            });
        }
        else if (sideA.IsNullable && !sideB.IsNullable)
        {
            // nullable → NOT NULL: caution
            reasons.Add(new RiskReason
            {
                Tier = RiskTier.Caution,
                Description = "Column changed from nullable to NOT NULL; may fail if NULL values exist.",
            });
        }

        // Collation
        if (!string.Equals(sideA.Collation, sideB.Collation, StringComparison.OrdinalIgnoreCase)
            && !(sideA.Collation is null && sideB.Collation is null))
        {
            reasons.Add(new RiskReason
            {
                Tier = RiskTier.Risky,
                Description = "Column collation changed.",
            });
        }

        // Default constraint changes
        bool hadDefault = sideA.DefaultConstraint is not null;
        bool hasDefault = sideB.DefaultConstraint is not null;

        if (!hadDefault && hasDefault)
        {
            reasons.Add(new RiskReason
            {
                Tier = RiskTier.Safe,
                Description = "Default constraint added.",
            });
        }
        else if (hadDefault && !hasDefault)
        {
            reasons.Add(new RiskReason
            {
                Tier = RiskTier.Risky,
                Description = "Default constraint removed.",
            });
        }
        else if (hadDefault && hasDefault
            && !string.Equals(sideA.DefaultConstraint!.Definition, sideB.DefaultConstraint!.Definition, StringComparison.Ordinal))
        {
            reasons.Add(new RiskReason
            {
                Tier = RiskTier.Risky,
                Description = "Default constraint definition changed.",
            });
        }

        if (reasons.Count == 0)
        {
            reasons.Add(new RiskReason
            {
                Tier = RiskTier.Safe,
                Description = "Column definition changed.",
            });
            return (RiskTier.Safe, reasons);
        }

        var maxTier = RiskTier.Safe;
        foreach (var r in reasons)
        {
            if (r.Tier > maxTier)
                maxTier = r.Tier;
        }

        return (maxTier, reasons);
    }
}
