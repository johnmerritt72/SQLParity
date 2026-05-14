using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SQLParity.Core.Model;

namespace SQLParity.Core.Sync;

public sealed class ScriptGenerationOptions
{
    public required string DestinationServer { get; init; }
    public required string DestinationDatabase { get; init; }
    public required string DestinationLabel { get; init; }
    public required string SourceServer { get; init; }
    public required string SourceDatabase { get; init; }
    public required string SourceLabel { get; init; }
}

public static class ScriptGenerator
{
    public static SyncScript Generate(IEnumerable<Change> changes, ScriptGenerationOptions options,
        IProgress<(int completed, int total, string current)>? progress = null)
    {
        var changeList = changes.ToList();
        var generatedAt = DateTime.UtcNow;

        int safeCount        = changeList.Count(c => c.Risk == RiskTier.Safe);
        int cautionCount     = changeList.Count(c => c.Risk == RiskTier.Caution);
        int riskyCount       = changeList.Count(c => c.Risk == RiskTier.Risky);
        int destructiveCount = changeList.Count(c => c.Risk == RiskTier.Destructive);

        var sb = new StringBuilder();

        // Header banner
        sb.AppendLine("/*");
        sb.AppendLine("  SQLParity — Schema Sync Script");
        sb.AppendLine($"  Generated (UTC): {generatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Source:          [{options.SourceLabel}] {options.SourceServer} / {options.SourceDatabase}");
        sb.AppendLine($"  Destination:     [{options.DestinationLabel}] {options.DestinationServer} / {options.DestinationDatabase}");
        sb.AppendLine($"  Total changes:   {changeList.Count}");
        sb.AppendLine($"    Safe:          {safeCount}");
        sb.AppendLine($"    Caution:       {cautionCount}");
        sb.AppendLine($"    Risky:         {riskyCount}");
        sb.AppendLine($"    Destructive:   {destructiveCount}");
        sb.AppendLine("*/");
        sb.AppendLine();

        // USE statement
        sb.AppendLine($"USE [{options.DestinationDatabase}]");
        sb.AppendLine("GO");
        sb.AppendLine();

        // Identify routines that need the stub/fill two-pass approach
        // (new or modified routines that may have circular dependencies)
        var routineTypes = new HashSet<ObjectType>
        {
            ObjectType.StoredProcedure,
            ObjectType.UserDefinedFunction,
            ObjectType.View,
        };

        // Prerequisite object types that must exist before any routine stubs
        var prerequisiteTypes = new HashSet<ObjectType>
        {
            ObjectType.Schema,
            ObjectType.UserDefinedDataType,
            ObjectType.UserDefinedTableType,
        };

        var newOrModifiedRoutines = changeList
            .Where(c => routineTypes.Contains(c.ObjectType)
                && (c.Status == ChangeStatus.New || c.Status == ChangeStatus.Modified))
            .ToList();

        var circularRoutines = DependencyOrderer.FindCircularDependencies(newOrModifiedRoutines);

        // Emit prerequisites (schemas, types) before stubs so schemas exist
        // for CREATE OR ALTER [Schema].[RoutineName] stubs.
        var prerequisites = changeList.Where(c =>
            prerequisiteTypes.Contains(c.ObjectType) && c.Status == ChangeStatus.New).ToList();
        var prerequisiteSet = new HashSet<Change>(prerequisites);

        if (prerequisites.Count > 0)
        {
            sb.AppendLine("-- ============================================");
            sb.AppendLine("-- Prerequisites: schemas, types");
            sb.AppendLine("-- ============================================");
            sb.AppendLine();

            foreach (var change in prerequisites)
            {
                // Comment in its own batch (followed by GO) so it never becomes
                // part of any stored module definition. Critical for procs, views,
                // functions, triggers whose text is preserved by SQL Server.
                sb.AppendLine($"-- [{change.Risk}] {change.Status} {change.ObjectType}: {change.Id}");
                sb.AppendLine("GO");
                sb.AppendLine(GetSql(change));
                sb.AppendLine("GO");
                sb.AppendLine();
            }
        }

        // Pass 1: Emit stubs for circular dependencies so metadata exists
        if (circularRoutines.Count > 0)
        {
            sb.AppendLine("-- ============================================");
            sb.AppendLine("-- Stubs for circular dependencies");
            sb.AppendLine("-- ============================================");
            sb.AppendLine();

            foreach (var change in circularRoutines)
            {
                sb.AppendLine($"-- Stub for {change.ObjectType}: {change.Id}");
                sb.AppendLine("GO");
                sb.AppendLine(GenerateStub(change));
                sb.AppendLine("GO");
                sb.AppendLine();
            }
        }

        // Main pass: each change with full DDL (skip prerequisites already emitted)
        sb.AppendLine("-- ============================================");
        sb.AppendLine("-- Main pass: all changes");
        sb.AppendLine("-- ============================================");
        sb.AppendLine();

        int completed = 0;
        foreach (var change in changeList)
        {
            if (prerequisiteSet.Contains(change))
            {
                completed++;
                progress?.Report((completed, changeList.Count, change.Id.ToString()));
                continue; // Already emitted above
            }

            sb.AppendLine($"-- [{change.Risk}] {change.Status} {change.ObjectType}: {change.Id}");
            sb.AppendLine("GO");

            string sql = GetSql(change);
            sb.AppendLine(sql);
            sb.AppendLine("GO");
            sb.AppendLine();

            completed++;
            progress?.Report((completed, changeList.Count, change.Id.ToString()));
        }

        sb.AppendLine("PRINT 'Script completed.'");
        sb.AppendLine("GO");

        return new SyncScript
        {
            SqlText = sb.ToString(),
            GeneratedAtUtc = generatedAt,
            DestinationDatabase = options.DestinationDatabase,
            DestinationServer = options.DestinationServer,
            TotalChanges = changeList.Count,
            DestructiveChanges = destructiveCount,
        };
    }

    private static string GetSql(Change change)
    {
        if (change.Status == ChangeStatus.New)
        {
            var newDdl = RewriteCreateNameIfPaired(change, change.DdlSideA ?? string.Empty);
            return ConvertToCreateOrAlter(newDdl, change.ObjectType);
        }

        if (change.Status == ChangeStatus.Modified)
        {
            if (change.ObjectType == ObjectType.Table && change.ColumnChanges.Count > 0)
                return AlterTableGenerator.GenerateForModifiedTable(change.Id.Schema, change.Id.Name, change.ColumnChanges);

            // Apply DdlSideA (the codebase convention: source-side DDL goes to destination).
            // RewriteCreateNameIfPaired is a no-op unless PairedFromName is set — for the
            // typo-rename pair case, it rewrites the CREATE name token to the DB's name.
            var modifiedDdl = RewriteCreateNameIfPaired(change, change.DdlSideA ?? string.Empty);
            return ConvertToCreateOrAlter(modifiedDdl, change.ObjectType);
        }

        // Dropped — generate a DROP statement
        return BuildDropStatement(change);
    }

    /// <summary>
    /// When a Change has PairedFromName set, rewrite the CREATE [OR ALTER]
    /// &lt;type&gt; [&lt;schema&gt;.]&lt;oldname&gt; token in the file's DDL to use
    /// change.Id.Name (the DB's correct name). Only the header token is
    /// rewritten; the rest of the body is left untouched so the user can spot
    /// any in-body references that also need fixing.
    /// </summary>
    internal static string RewriteCreateNameIfPaired(Change change, string ddl)
    {
        if (string.IsNullOrEmpty(change.PairedFromName) || string.IsNullOrEmpty(ddl))
            return ddl;

        // Regex anchored on CREATE [OR ALTER] <type-keyword> [<schema>.]<oldname>.
        // Allows brackets, whitespace, and case-insensitive object-type keyword.
        // Only matches the first occurrence (the header).
        string oldName = System.Text.RegularExpressions.Regex.Escape(change.PairedFromName);
        var pattern = @"(?im)(\bCREATE\s+(?:OR\s+ALTER\s+)?(?:PROC|PROCEDURE|VIEW|FUNCTION|SEQUENCE|SYNONYM|TYPE)\s+(?:\[?[A-Za-z_][A-Za-z0-9_]*\]?\s*\.\s*)?)\[?" + oldName + @"\]?";
        var rx = new System.Text.RegularExpressions.Regex(pattern);
        var match = rx.Match(ddl);
        if (!match.Success)
        {
            // Defensive: couldn't find the header token. Emit a warning comment
            // and pass through the original DDL so the user sees both.
            return "-- WARNING: paired typo-rename — could not auto-rewrite CREATE name. Fix the .sql file's CREATE statement before applying."
                + System.Environment.NewLine + ddl;
        }

        // Replace only the matched section: prefix (capture 1) + correct name.
        // Preserve the original bracket style from the prefix captured in group 1;
        // the name itself is emitted without extra brackets so "dbo.Foo" stays as-is.
        string replacement = match.Groups[1].Value + change.Id.Name;
        return ddl.Substring(0, match.Index) + replacement + ddl.Substring(match.Index + match.Length);
    }

    /// <summary>
    /// Converts CREATE PROCEDURE/FUNCTION/VIEW/TRIGGER to CREATE OR ALTER for safe deployment.
    /// Works for both new and modified objects — CREATE OR ALTER creates if missing, alters if exists.
    /// Handles variable whitespace between CREATE and the keyword (SMO often generates extra spaces).
    /// </summary>
    /// <summary>
    /// Generates a minimal stub for a routine so the object exists in the catalog
    /// (satisfies circular dependency references) before the real body is applied.
    /// </summary>
    private static string GenerateStub(Change change)
    {
        var name = FormatObjectName(change);
        switch (change.ObjectType)
        {
            case ObjectType.StoredProcedure:
                return $"CREATE OR ALTER PROCEDURE {name} AS BEGIN RETURN; END";
            case ObjectType.UserDefinedFunction:
                // Scalar function stub — returns NULL
                return $"CREATE OR ALTER FUNCTION {name}() RETURNS INT AS BEGIN RETURN NULL; END";
            case ObjectType.View:
                // View stub — minimal SELECT
                return $"CREATE OR ALTER VIEW {name} AS SELECT 1 AS __stub";
            case ObjectType.Trigger:
                // Triggers can't be stubbed independently — skip
                return $"-- Cannot stub trigger {name}; will be created in main pass";
            default:
                return $"-- Cannot stub {change.ObjectType} {name}";
        }
    }

    private static string? GetRoutineKeyword(ObjectType type)
    {
        switch (type)
        {
            case ObjectType.StoredProcedure: return "PROCEDURE";
            case ObjectType.UserDefinedFunction: return "FUNCTION";
            case ObjectType.View: return "VIEW";
            case ObjectType.Trigger: return "TRIGGER";
            default: return null;
        }
    }

    private static string FormatObjectName(Change change)
    {
        return $"[{change.Id.Schema}].[{change.Id.Name}]";
    }

    internal static string ConvertToCreateOrAlter(string ddl, ObjectType objectType)
    {
        if (string.IsNullOrWhiteSpace(ddl))
            return ddl;

        // Keyword pattern — supports SQL Server shorthand (PROC for PROCEDURE)
        string keywordPattern;
        string replacementKeyword;
        switch (objectType)
        {
            case ObjectType.StoredProcedure:
                keywordPattern = @"PROC(EDURE)?";
                replacementKeyword = "PROCEDURE";
                break;
            case ObjectType.UserDefinedFunction:
                keywordPattern = "FUNCTION";
                replacementKeyword = "FUNCTION";
                break;
            case ObjectType.View:
                keywordPattern = "VIEW";
                replacementKeyword = "VIEW";
                break;
            case ObjectType.Trigger:
                keywordPattern = "TRIGGER";
                replacementKeyword = "TRIGGER";
                break;
            default: return ddl;
        }

        // Match "CREATE" + any whitespace + keyword (case-insensitive, word boundary after)
        var regex = new System.Text.RegularExpressions.Regex(
            @"CREATE\s+" + keywordPattern + @"\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace only the first occurrence
        if (regex.IsMatch(ddl))
            return regex.Replace(ddl, "CREATE OR ALTER " + replacementKeyword, 1);

        return ddl;
    }

    private static string BuildDropStatement(Change change)
    {
        var id = change.Id;

        switch (change.ObjectType)
        {
            case ObjectType.Table:
                return $"DROP TABLE [{id.Schema}].[{id.Name}]";

            case ObjectType.View:
                return $"DROP VIEW [{id.Schema}].[{id.Name}]";

            case ObjectType.StoredProcedure:
                return $"DROP PROCEDURE [{id.Schema}].[{id.Name}]";

            case ObjectType.UserDefinedFunction:
                return $"DROP FUNCTION [{id.Schema}].[{id.Name}]";

            case ObjectType.Schema:
                return $"DROP SCHEMA [{id.Name}]";

            case ObjectType.Sequence:
                return $"DROP SEQUENCE [{id.Schema}].[{id.Name}]";

            case ObjectType.Synonym:
                return $"DROP SYNONYM [{id.Schema}].[{id.Name}]";

            case ObjectType.Trigger:
                // DML trigger: Schema = tableSchema, Parent = tableName, Name = triggerName
                return $"DROP TRIGGER [{id.Schema}].[{id.Name}]";

            case ObjectType.Index:
                return $"DROP INDEX [{id.Name}] ON [{id.Schema}].[{id.Parent}]";

            case ObjectType.ForeignKey:
                return $"ALTER TABLE [{id.Schema}].[{id.Parent}] DROP CONSTRAINT [{id.Name}]";

            case ObjectType.CheckConstraint:
                return $"ALTER TABLE [{id.Schema}].[{id.Parent}] DROP CONSTRAINT [{id.Name}]";

            case ObjectType.UserDefinedDataType:
                return $"DROP TYPE [{id.Schema}].[{id.Name}]";

            case ObjectType.UserDefinedTableType:
                return $"DROP TYPE [{id.Schema}].[{id.Name}]";

            default:
                return $"-- DROP {change.ObjectType} [{id.Schema}].[{id.Name}]  (unhandled type)";
        }
    }
}
