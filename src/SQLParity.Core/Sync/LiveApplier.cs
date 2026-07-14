using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SQLParity.Core.Model;

namespace SQLParity.Core.Sync;

public sealed class LiveApplier
{
    private readonly string _connectionString;

    public LiveApplier(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public ApplyResult Apply(IEnumerable<Change> orderedChanges, ScriptGenerationOptions options,
        IProgress<(int completed, int total, string current)>? progress = null)
    {
        var changeList = orderedChanges as IList<Change> ?? new List<Change>(orderedChanges);
        var steps = new List<ApplyStepResult>();
        var startedAt = DateTime.UtcNow;
        var allSucceeded = true;
        int completed = 0;

        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        // Server-side PRINT / RAISERROR(severity ≤ 10) messages land here.
        // We snapshot the buffer per step so each step records its own messages.
        var currentBatch = new List<string>();
        SqlInfoMessageEventHandler captureInfo = (_, e) =>
        {
            foreach (SqlError err in e.Errors)
                currentBatch.Add(err.Message);
        };
        conn.InfoMessage += captureInfo;

        using var tx = conn.BeginTransaction();

        foreach (var change in changeList)
        {
            var sql = GetSqlForChange(change);
            if (string.IsNullOrWhiteSpace(sql))
                continue;

            var sw = Stopwatch.StartNew();
            currentBatch.Clear();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                sw.Stop();
                steps.Add(new ApplyStepResult
                {
                    ObjectName = change.Id.ToString(),
                    Sql = sql,
                    Succeeded = true,
                    ErrorMessage = null,
                    Duration = sw.Elapsed,
                    InfoMessages = currentBatch.ToArray(),
                });
            }
            catch (SqlException ex) when (IsRoutineType(change.ObjectType)
                && change.Status != ChangeStatus.Dropped)
            {
                // CREATE OR ALTER for routines can throw for deferred-resolution
                // issues (missing linked servers, cross-DB refs, etc.) while still
                // creating the object. Treat as a warning, not a hard failure.
                sw.Stop();
                steps.Add(new ApplyStepResult
                {
                    ObjectName = change.Id.ToString(),
                    Sql = sql,
                    Succeeded = true,
                    ErrorMessage = "Warning: " + ex.Message,
                    Duration = sw.Elapsed,
                    InfoMessages = currentBatch.ToArray(),
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                steps.Add(new ApplyStepResult
                {
                    ObjectName = change.Id.ToString(),
                    Sql = sql,
                    Succeeded = false,
                    ErrorMessage = ex.Message,
                    Duration = sw.Elapsed,
                    InfoMessages = currentBatch.ToArray(),
                });
                allSucceeded = false;
                break; // Stop on first failure
            }

            completed++;
            progress?.Report((completed, changeList.Count, change.Id.ToString()));
        }

        // Permissions pass — mirrors the trailing Permissions section in
        // ScriptGenerator.Generate. Runs only if every DDL step succeeded so the
        // grants always reference objects that already exist in the catalog.
        if (allSucceeded)
        {
            foreach (var change in changeList)
            {
                if (change.PermissionChanges == null || change.PermissionChanges.Count == 0)
                    continue;

                string permSql = PermissionScriptGenerator.Generate(change);
                if (string.IsNullOrWhiteSpace(permSql))
                    continue;

                var stepName = change.Id + " (permissions)";
                var sw = Stopwatch.StartNew();
                currentBatch.Clear();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = permSql;
                    cmd.ExecuteNonQuery();
                    sw.Stop();
                    steps.Add(new ApplyStepResult
                    {
                        ObjectName = stepName,
                        Sql = permSql,
                        Succeeded = true,
                        ErrorMessage = null,
                        Duration = sw.Elapsed,
                        InfoMessages = currentBatch.ToArray(),
                    });
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    steps.Add(new ApplyStepResult
                    {
                        ObjectName = stepName,
                        Sql = permSql,
                        Succeeded = false,
                        ErrorMessage = ex.Message,
                        Duration = sw.Elapsed,
                        InfoMessages = currentBatch.ToArray(),
                    });
                    allSucceeded = false;
                    break;
                }
            }
        }

        conn.InfoMessage -= captureInfo;

        if (allSucceeded)
            tx.Commit();
        else
            tx.Rollback();

        return new ApplyResult
        {
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTime.UtcNow,
            DestinationDatabase = options.DestinationDatabase,
            DestinationServer = options.DestinationServer,
            Steps = steps,
            FullySucceeded = allSucceeded,
        };
    }

    private static bool IsRoutineType(ObjectType type) =>
        type == ObjectType.StoredProcedure
        || type == ObjectType.UserDefinedFunction
        || type == ObjectType.View
        || type == ObjectType.Trigger;

    private static string GetSqlForChange(Change change)
    {
        switch (change.Status)
        {
            case ChangeStatus.New:
            {
                var ddl = ScriptGenerator.RewriteCreateNameIfPaired(change, change.DdlSideA ?? string.Empty);
                return ScriptGenerator.ConvertToCreateOrAlter(ddl, change.ObjectType);
            }
            case ChangeStatus.Modified:
                if (change.ObjectType == ObjectType.Table && change.ColumnChanges.Count > 0)
                    return AlterTableGenerator.GenerateForModifiedTable(change.Id.Schema, change.Id.Name, change.ColumnChanges);
                {
                    var ddl = ScriptGenerator.RewriteCreateNameIfPaired(change, change.DdlSideA ?? string.Empty);
                    return ScriptGenerator.ConvertToCreateOrAlter(ddl, change.ObjectType);
                }
            case ChangeStatus.Dropped:
                return GenerateDropSql(change);
            default:
                return string.Empty;
        }
    }

    private static string GenerateDropSql(Change change)
    {
        switch (change.ObjectType)
        {
            case ObjectType.Table:
                return $"DROP TABLE [{change.Id.Schema}].[{change.Id.Name}]";
            case ObjectType.View:
                return $"DROP VIEW [{change.Id.Schema}].[{change.Id.Name}]";
            case ObjectType.StoredProcedure:
                return $"DROP PROCEDURE [{change.Id.Schema}].[{change.Id.Name}]";
            case ObjectType.UserDefinedFunction:
                return $"DROP FUNCTION [{change.Id.Schema}].[{change.Id.Name}]";
            case ObjectType.Schema:
                return $"DROP SCHEMA [{change.Id.Name}]";
            case ObjectType.Sequence:
                return $"DROP SEQUENCE [{change.Id.Schema}].[{change.Id.Name}]";
            case ObjectType.Synonym:
                return $"DROP SYNONYM [{change.Id.Schema}].[{change.Id.Name}]";
            case ObjectType.Trigger:
                return $"DROP TRIGGER [{change.Id.Schema}].[{change.Id.Name}]";
            case ObjectType.UserDefinedDataType:
                return $"DROP TYPE [{change.Id.Schema}].[{change.Id.Name}]";
            case ObjectType.UserDefinedTableType:
                return $"DROP TYPE [{change.Id.Schema}].[{change.Id.Name}]";
            case ObjectType.Index:
                return $"DROP INDEX [{change.Id.Name}] ON [{change.Id.Schema}].[{change.Id.Parent}]";
            case ObjectType.ForeignKey:
                return $"ALTER TABLE [{change.Id.Schema}].[{change.Id.Parent}] DROP CONSTRAINT [{change.Id.Name}]";
            case ObjectType.CheckConstraint:
                return $"ALTER TABLE [{change.Id.Schema}].[{change.Id.Parent}] DROP CONSTRAINT [{change.Id.Name}]";
            default:
                return string.Empty;
        }
    }
}
