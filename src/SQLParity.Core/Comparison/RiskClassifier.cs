using System;
using System.Collections.Generic;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Classifies the risk of a top-level schema change.
/// </summary>
public static class RiskClassifier
{
    public static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) Classify(Change change)
    {
        return change.Status switch
        {
            ChangeStatus.New => ClassifyNew(change),
            ChangeStatus.Dropped => ClassifyDropped(change),
            ChangeStatus.Modified => ClassifyModified(change),
            _ => (RiskTier.Safe, Array.Empty<RiskReason>()),
        };
    }

    private static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) ClassifyNew(Change change)
    {
        return (RiskTier.Safe, new[]
        {
            new RiskReason
            {
                Tier = RiskTier.Safe,
                Description = $"New {change.ObjectType} will be created.",
            },
        });
    }

    private static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) ClassifyDropped(Change change)
    {
        return change.ObjectType switch
        {
            ObjectType.Table => (RiskTier.Destructive, new[]
            {
                new RiskReason
                {
                    Tier = RiskTier.Destructive,
                    Description = "Table will be dropped. All data will be lost.",
                },
            }),

            ObjectType.Index => (RiskTier.Risky, new[]
            {
                new RiskReason
                {
                    Tier = RiskTier.Risky,
                    Description = "Index removal may impact query performance.",
                },
            }),

            ObjectType.ForeignKey => (RiskTier.Destructive, new[]
            {
                new RiskReason
                {
                    Tier = RiskTier.Destructive,
                    Description = "Foreign key constraint will be removed.",
                },
            }),

            ObjectType.CheckConstraint => (RiskTier.Destructive, new[]
            {
                new RiskReason
                {
                    Tier = RiskTier.Destructive,
                    Description = "Check constraint will be removed.",
                },
            }),

            // All other dropped object types → Destructive
            _ => (RiskTier.Destructive, new[]
            {
                new RiskReason
                {
                    Tier = RiskTier.Destructive,
                    Description = $"{change.ObjectType} will be removed.",
                },
            }),
        };
    }

    private static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) ClassifyModified(Change change)
    {
        return change.ObjectType switch
        {
            ObjectType.View or ObjectType.StoredProcedure or ObjectType.UserDefinedFunction =>
                (RiskTier.Caution, new[]
                {
                    new RiskReason
                    {
                        Tier = RiskTier.Caution,
                        Description = $"{change.ObjectType} definition changed. May affect dependent objects.",
                    },
                }),

            ObjectType.Index =>
                (RiskTier.Caution, new[]
                {
                    new RiskReason
                    {
                        Tier = RiskTier.Caution,
                        Description = "Index definition changed. Will require drop and recreate.",
                    },
                }),

            ObjectType.Table => ClassifyModifiedTable(change),

            _ => (RiskTier.Safe, new[]
            {
                new RiskReason
                {
                    Tier = RiskTier.Safe,
                    Description = $"{change.ObjectType} definition changed.",
                },
            }),
        };
    }

    private static (RiskTier Tier, IReadOnlyList<RiskReason> Reasons) ClassifyModifiedTable(Change change)
    {
        if (change.ColumnChanges.Count == 0)
        {
            return (RiskTier.Safe, new[]
            {
                new RiskReason
                {
                    Tier = RiskTier.Safe,
                    Description = "Table DDL changed without column modifications.",
                },
            });
        }

        var allReasons = new List<RiskReason>();
        var maxTier = RiskTier.Safe;

        foreach (var col in change.ColumnChanges)
        {
            if (col.Risk > maxTier)
                maxTier = col.Risk;

            allReasons.AddRange(col.Reasons);
        }

        return (maxTier, allReasons);
    }
}
