using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using Microsoft.Data.SqlClient;
using SQLParity.Core.Model;

namespace SQLParity.Core;

/// <summary>
/// Reads all table metadata (columns, indexes, FKs, checks, triggers) in bulk
/// using direct SQL queries instead of per-table SMO enumeration.
/// This reduces round-trips from 5×N (where N = table count) to just 5 queries.
/// </summary>
internal static class BulkTableMetadataReader
{
    public static List<TableModel> ReadAllTables(string connectionString, string databaseName,
        IProgress<SchemaReadProgress>? progress, ref int completed, int totalObjects,
        CancellationToken ct)
    {
        var csb = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = databaseName,
            TrustServerCertificate = true,
        };

        // Dictionaries keyed by (schema, tableName) for grouping
        var tableKeys = new List<(string Schema, string Name)>();
        var columnsByTable = new Dictionary<(string, string), List<ColumnModel>>(SchemaTableComparer.Instance);
        var indexesByTable = new Dictionary<(string, string), List<IndexModel>>(SchemaTableComparer.Instance);
        var fksByTable = new Dictionary<(string, string), List<ForeignKeyModel>>(SchemaTableComparer.Instance);
        var checksByTable = new Dictionary<(string, string), List<CheckConstraintModel>>(SchemaTableComparer.Instance);
        var triggersByTable = new Dictionary<(string, string), List<TriggerModel>>(SchemaTableComparer.Instance);

        using (var conn = new SqlConnection(csb.ConnectionString))
        {
            conn.Open();

            // 1. Read all columns
            ReadAllColumns(conn, columnsByTable, tableKeys, ct);

            // 2. Read all indexes + their columns
            ReadAllIndexes(conn, indexesByTable, ct);

            // 3. Read all foreign keys + their columns
            ReadAllForeignKeys(conn, fksByTable, ct);

            // 4. Read all check constraints
            ReadAllChecks(conn, checksByTable, ct);

            // 5. Read all triggers
            ReadAllTriggers(conn, triggersByTable, ct);
        }

        // Build TableModel objects
        var result = new List<TableModel>(tableKeys.Count);
        foreach (var (schema, name) in tableKeys)
        {
            ct.ThrowIfCancellationRequested();

            completed++;
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = $"Tables: [{schema}].[{name}]",
                CompletedItems = completed,
                TotalItems = totalObjects,
            });

            var key = (schema, name);
            var cols = columnsByTable.TryGetValue(key, out var c) ? c : new List<ColumnModel>();
            var idxs = indexesByTable.TryGetValue(key, out var i) ? i : new List<IndexModel>();
            var fks = fksByTable.TryGetValue(key, out var f) ? f : new List<ForeignKeyModel>();
            var chks = checksByTable.TryGetValue(key, out var ch) ? ch : new List<CheckConstraintModel>();
            var trigs = triggersByTable.TryGetValue(key, out var tr) ? tr : new List<TriggerModel>();

            // Build a temporary TableModel to feed the generator (Ddl is what we're computing)
            var modelForDdl = new TableModel
            {
                Id = SchemaQualifiedName.TopLevel(schema, name),
                Schema = schema,
                Name = name,
                Ddl = string.Empty,
                Columns = cols,
                Indexes = idxs,
                ForeignKeys = fks,
                CheckConstraints = chks,
                Triggers = trigs,
            };

            string ddl;
            try
            {
                ddl = SQLParity.Core.Comparison.CreateTableGenerator.Generate(modelForDdl);
            }
            catch (Exception ex)
            {
                // Defensive: a malformed table (e.g. zero columns) shouldn't crash the whole bulk read.
                ddl = $"-- Could not script table [{schema}].[{name}]: {ex.Message}";
            }

            result.Add(new TableModel
            {
                Id = modelForDdl.Id,
                Schema = schema,
                Name = name,
                Ddl = ddl,
                Columns = cols,
                Indexes = idxs,
                ForeignKeys = fks,
                CheckConstraints = chks,
                Triggers = trigs,
            });
        }

        return result;
    }

    #region Column Query

    private const string ColumnsSql = @"
SELECT
    SCHEMA_NAME(t.schema_id) AS TableSchema,
    t.name AS TableName,
    c.name AS ColumnName,
    TYPE_NAME(c.user_type_id) AS DataType,
    c.max_length AS MaxLength,
    c.precision AS [Precision],
    c.scale AS Scale,
    c.is_nullable AS IsNullable,
    c.is_identity AS IsIdentity,
    CAST(ISNULL(ic.seed_value, 0) AS BIGINT) AS IdentitySeed,
    CAST(ISNULL(ic.increment_value, 0) AS BIGINT) AS IdentityIncrement,
    c.is_computed AS IsComputed,
    cc.definition AS ComputedText,
    ISNULL(cc.is_persisted, 0) AS IsPersisted,
    c.collation_name AS Collation,
    dc.name AS DefaultConstraintName,
    dc.definition AS DefaultDefinition,
    c.column_id AS OrdinalPosition
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE t.is_ms_shipped = 0
ORDER BY SCHEMA_NAME(t.schema_id), t.name, c.column_id";

    private static void ReadAllColumns(SqlConnection conn,
        Dictionary<(string, string), List<ColumnModel>> columnsByTable,
        List<(string Schema, string Name)> tableKeys,
        CancellationToken ct)
    {
        var seenTables = new HashSet<(string, string)>(SchemaTableComparer.Instance);

        using var cmd = new SqlCommand(ColumnsSql, conn);
        cmd.CommandTimeout = 0; // no timeout — schema enumeration on large DBs can exceed 120s
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var key = (schema, tableName);

            if (seenTables.Add(key))
                tableKeys.Add(key);

            if (!columnsByTable.TryGetValue(key, out var list))
            {
                list = new List<ColumnModel>();
                columnsByTable[key] = list;
            }

            var colName = reader.GetString(2);
            var rawDataType = reader.GetString(3);
            var maxLength = reader.GetInt16(4);
            var precision = reader.GetByte(5);
            var scale = reader.GetByte(6);
            var isNullable = reader.GetBoolean(7);
            var isIdentity = reader.GetBoolean(8);
            var identitySeed = reader.GetInt64(9);
            var identityIncrement = reader.GetInt64(10);
            var isComputed = reader.GetBoolean(11);
            var computedText = reader.IsDBNull(12) ? null : reader.GetString(12);
            var isPersisted = reader.GetBoolean(13);
            var collation = reader.IsDBNull(14) ? null : reader.GetString(14);
            var defaultName = reader.IsDBNull(15) ? null : reader.GetString(15);
            var defaultDef = reader.IsDBNull(16) ? null : reader.GetString(16);
            var ordinal = reader.GetInt32(17);

            // Map SQL type name to SMO-compatible SqlDataType enum name
            var dataType = MapSqlTypeName(rawDataType);

            // Adjust max_length for Unicode types (nchar, nvarchar) — sys.columns
            // stores byte count, but SMO reports character count
            int adjustedMaxLength = maxLength;
            if (maxLength > 0 && IsUnicodeType(rawDataType))
                adjustedMaxLength = maxLength / 2;

            DefaultConstraintModel? defaultConstraint = null;
            if (defaultName != null && defaultDef != null)
            {
                defaultConstraint = new DefaultConstraintModel
                {
                    Name = defaultName,
                    Definition = defaultDef,
                };
            }

            list.Add(new ColumnModel
            {
                Id = SchemaQualifiedName.Child(schema, tableName, colName),
                Name = colName,
                DataType = dataType,
                MaxLength = adjustedMaxLength,
                Precision = precision,
                Scale = scale,
                IsNullable = isNullable,
                IsIdentity = isIdentity,
                IdentitySeed = isIdentity ? identitySeed : 0L,
                IdentityIncrement = isIdentity ? identityIncrement : 0L,
                IsComputed = isComputed,
                ComputedText = isComputed ? computedText : null,
                IsPersisted = isPersisted,
                Collation = string.IsNullOrEmpty(collation) ? null : collation,
                DefaultConstraint = defaultConstraint,
                OrdinalPosition = ordinal - 1, // 1-based to 0-based
            });
        }
    }

    #endregion

    #region Index Query

    private const string IndexesSql = @"
SELECT
    SCHEMA_NAME(t.schema_id) AS TableSchema,
    t.name AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique,
    i.is_primary_key AS IsPrimaryKey,
    i.is_unique_constraint AS IsUniqueConstraint,
    i.has_filter AS HasFilter,
    i.filter_definition AS FilterDefinition,
    CASE WHEN i.type IN (1, 5) THEN 1 ELSE 0 END AS IsClustered,
    ic2.name AS ColumnName,
    ic.is_descending_key AS IsDescending,
    ic.is_included_column AS IsIncluded
FROM sys.tables t
JOIN sys.indexes i ON i.object_id = t.object_id
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns ic2 ON ic2.object_id = ic.object_id AND ic2.column_id = ic.column_id
WHERE t.is_ms_shipped = 0 AND i.name IS NOT NULL
ORDER BY SCHEMA_NAME(t.schema_id), t.name, i.name, ic.key_ordinal, ic.index_column_id";

    private static void ReadAllIndexes(SqlConnection conn,
        Dictionary<(string, string), List<IndexModel>> indexesByTable,
        CancellationToken ct)
    {
        // Temp structure to accumulate columns per index
        var indexColumns = new Dictionary<(string, string, string), List<IndexedColumnModel>>(StringTuple3Comparer.Instance);
        var indexMeta = new Dictionary<(string, string, string), (string IndexType, bool IsUnique, bool IsPK, bool IsUC, bool HasFilter, string? FilterDef, bool IsClustered)>(StringTuple3Comparer.Instance);
        // Preserve insertion order of indexes per table
        var indexOrder = new Dictionary<(string, string), List<string>>(SchemaTableComparer.Instance);

        using var cmd = new SqlCommand(IndexesSql, conn);
        cmd.CommandTimeout = 0; // no timeout — schema enumeration on large DBs can exceed 120s
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var indexName = reader.GetString(2);
            var tableKey = (schema, tableName);
            var indexKey = (schema, tableName, indexName);

            if (!indexMeta.ContainsKey(indexKey))
            {
                var typeDesc = reader.GetString(3);
                indexMeta[indexKey] = (
                    IndexType: MapIndexType(typeDesc),
                    IsUnique: reader.GetBoolean(4),
                    IsPK: reader.GetBoolean(5),
                    IsUC: reader.GetBoolean(6),
                    HasFilter: reader.GetBoolean(7),
                    FilterDef: reader.IsDBNull(8) ? null : reader.GetString(8),
                    IsClustered: reader.GetInt32(9) == 1
                );

                if (!indexOrder.TryGetValue(tableKey, out var orderList))
                {
                    orderList = new List<string>();
                    indexOrder[tableKey] = orderList;
                }
                orderList.Add(indexName);
            }

            if (!indexColumns.TryGetValue(indexKey, out var colList))
            {
                colList = new List<IndexedColumnModel>();
                indexColumns[indexKey] = colList;
            }

            colList.Add(new IndexedColumnModel
            {
                Name = reader.GetString(10),
                IsDescending = reader.GetBoolean(11),
                IsIncluded = reader.GetBoolean(12),
            });
        }

        // Build IndexModel objects grouped by table
        foreach (var kvp in indexOrder)
        {
            var tableKey = kvp.Key;
            var names = kvp.Value;
            var list = new List<IndexModel>(names.Count);
            foreach (var idxName in names)
            {
                var indexKey = (tableKey.Item1, tableKey.Item2, idxName);
                var meta = indexMeta[indexKey];
                list.Add(new IndexModel
                {
                    Id = SchemaQualifiedName.Child(tableKey.Item1, tableKey.Item2, idxName),
                    Name = idxName,
                    IndexType = meta.IndexType,
                    IsClustered = meta.IsClustered,
                    IsUnique = meta.IsUnique,
                    IsPrimaryKey = meta.IsPK,
                    IsUniqueConstraint = meta.IsUC,
                    HasFilter = meta.HasFilter,
                    FilterDefinition = meta.HasFilter ? meta.FilterDef : null,
                    Columns = indexColumns[indexKey],
                    Ddl = string.Empty,
                });
            }
            indexesByTable[tableKey] = list;
        }
    }

    #endregion

    #region Foreign Key Query

    private const string ForeignKeysSql = @"
SELECT
    SCHEMA_NAME(t.schema_id) AS TableSchema,
    t.name AS TableName,
    fk.name AS FKName,
    SCHEMA_NAME(rt.schema_id) AS ReferencedTableSchema,
    rt.name AS ReferencedTableName,
    fk.delete_referential_action_desc AS DeleteAction,
    fk.update_referential_action_desc AS UpdateAction,
    CASE WHEN fk.is_disabled = 0 THEN 1 ELSE 0 END AS IsEnabled,
    COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS LocalColumn,
    COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS ReferencedColumn
FROM sys.tables t
JOIN sys.foreign_keys fk ON fk.parent_object_id = t.object_id
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
WHERE t.is_ms_shipped = 0
ORDER BY SCHEMA_NAME(t.schema_id), t.name, fk.name, fkc.constraint_column_id";

    private static void ReadAllForeignKeys(SqlConnection conn,
        Dictionary<(string, string), List<ForeignKeyModel>> fksByTable,
        CancellationToken ct)
    {
        var fkColumns = new Dictionary<(string, string, string), List<ForeignKeyColumnModel>>(StringTuple3Comparer.Instance);
        var fkMeta = new Dictionary<(string, string, string), (string RefSchema, string RefTable, string DeleteAction, string UpdateAction, bool IsEnabled)>(StringTuple3Comparer.Instance);
        var fkOrder = new Dictionary<(string, string), List<string>>(SchemaTableComparer.Instance);

        using var cmd = new SqlCommand(ForeignKeysSql, conn);
        cmd.CommandTimeout = 0; // no timeout — schema enumeration on large DBs can exceed 120s
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var fkName = reader.GetString(2);
            var tableKey = (schema, tableName);
            var fkKey = (schema, tableName, fkName);

            if (!fkMeta.ContainsKey(fkKey))
            {
                fkMeta[fkKey] = (
                    RefSchema: reader.GetString(3),
                    RefTable: reader.GetString(4),
                    DeleteAction: MapReferentialAction(reader.GetString(5)),
                    UpdateAction: MapReferentialAction(reader.GetString(6)),
                    IsEnabled: reader.GetInt32(7) == 1
                );

                if (!fkOrder.TryGetValue(tableKey, out var orderList))
                {
                    orderList = new List<string>();
                    fkOrder[tableKey] = orderList;
                }
                orderList.Add(fkName);
            }

            if (!fkColumns.TryGetValue(fkKey, out var colList))
            {
                colList = new List<ForeignKeyColumnModel>();
                fkColumns[fkKey] = colList;
            }

            colList.Add(new ForeignKeyColumnModel
            {
                LocalColumn = reader.GetString(8),
                ReferencedColumn = reader.GetString(9),
            });
        }

        foreach (var kvp in fkOrder)
        {
            var tableKey = kvp.Key;
            var names = kvp.Value;
            var list = new List<ForeignKeyModel>(names.Count);
            foreach (var fkName in names)
            {
                var fkKey = (tableKey.Item1, tableKey.Item2, fkName);
                var meta = fkMeta[fkKey];
                list.Add(new ForeignKeyModel
                {
                    Id = SchemaQualifiedName.Child(tableKey.Item1, tableKey.Item2, fkName),
                    Name = fkName,
                    ReferencedTableSchema = meta.RefSchema,
                    ReferencedTableName = meta.RefTable,
                    DeleteAction = meta.DeleteAction,
                    UpdateAction = meta.UpdateAction,
                    IsEnabled = meta.IsEnabled,
                    Columns = fkColumns[fkKey],
                    Ddl = string.Empty,
                });
            }
            fksByTable[tableKey] = list;
        }
    }

    #endregion

    #region Check Constraint Query

    private const string ChecksSql = @"
SELECT
    SCHEMA_NAME(t.schema_id) AS TableSchema,
    t.name AS TableName,
    cc.name AS CheckName,
    cc.definition AS Definition,
    CASE WHEN cc.is_disabled = 0 THEN 1 ELSE 0 END AS IsEnabled
FROM sys.tables t
JOIN sys.check_constraints cc ON cc.parent_object_id = t.object_id
WHERE t.is_ms_shipped = 0
ORDER BY SCHEMA_NAME(t.schema_id), t.name, cc.name";

    private static void ReadAllChecks(SqlConnection conn,
        Dictionary<(string, string), List<CheckConstraintModel>> checksByTable,
        CancellationToken ct)
    {
        using var cmd = new SqlCommand(ChecksSql, conn);
        cmd.CommandTimeout = 0; // no timeout — schema enumeration on large DBs can exceed 120s
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var key = (schema, tableName);

            if (!checksByTable.TryGetValue(key, out var list))
            {
                list = new List<CheckConstraintModel>();
                checksByTable[key] = list;
            }

            var checkName = reader.GetString(2);
            list.Add(new CheckConstraintModel
            {
                Id = SchemaQualifiedName.Child(schema, tableName, checkName),
                Name = checkName,
                Definition = reader.GetString(3),
                IsEnabled = reader.GetInt32(4) == 1,
                Ddl = string.Empty,
            });
        }
    }

    #endregion

    #region Trigger Query

    private const string TriggersSql = @"
SELECT
    SCHEMA_NAME(t.schema_id) AS TableSchema,
    t.name AS TableName,
    tr.name AS TriggerName,
    CASE WHEN tr.is_disabled = 0 THEN 1 ELSE 0 END AS IsEnabled,
    OBJECTPROPERTY(tr.object_id, 'ExecIsInsertTrigger') AS FiresOnInsert,
    OBJECTPROPERTY(tr.object_id, 'ExecIsUpdateTrigger') AS FiresOnUpdate,
    OBJECTPROPERTY(tr.object_id, 'ExecIsDeleteTrigger') AS FiresOnDelete,
    OBJECT_DEFINITION(tr.object_id) AS TriggerDdl
FROM sys.tables t
JOIN sys.triggers tr ON tr.parent_id = t.object_id
WHERE t.is_ms_shipped = 0 AND tr.is_ms_shipped = 0
ORDER BY SCHEMA_NAME(t.schema_id), t.name, tr.name";

    private static void ReadAllTriggers(SqlConnection conn,
        Dictionary<(string, string), List<TriggerModel>> triggersByTable,
        CancellationToken ct)
    {
        using var cmd = new SqlCommand(TriggersSql, conn);
        cmd.CommandTimeout = 0; // no timeout — schema enumeration on large DBs can exceed 120s
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var key = (schema, tableName);

            if (!triggersByTable.TryGetValue(key, out var list))
            {
                list = new List<TriggerModel>();
                triggersByTable[key] = list;
            }

            var trigName = reader.GetString(2);
            var ddl = reader.IsDBNull(7) ? $"-- Could not script trigger [{trigName}]" : reader.GetString(7);

            list.Add(new TriggerModel
            {
                Id = SchemaQualifiedName.Child(schema, tableName, trigName),
                Name = trigName,
                IsEnabled = reader.GetInt32(3) == 1,
                FiresOnInsert = reader.GetInt32(4) == 1,
                FiresOnUpdate = reader.GetInt32(5) == 1,
                FiresOnDelete = reader.GetInt32(6) == 1,
                Ddl = ddl,
            });
        }
    }

    #endregion

    #region Mapping helpers

    /// <summary>
    /// Maps SQL Server TYPE_NAME() output (e.g. "int", "nvarchar", "datetime2")
    /// to SMO SqlDataType enum names (e.g. "Int", "NVarChar", "DateTime2").
    /// Uses a dictionary for known types and falls back to TextInfo.ToTitleCase.
    /// </summary>
    private static readonly Dictionary<string, string> SqlTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bigint"] = "BigInt",
        ["binary"] = "Binary",
        ["bit"] = "Bit",
        ["char"] = "Char",
        ["date"] = "Date",
        ["datetime"] = "DateTime",
        ["datetime2"] = "DateTime2",
        ["datetimeoffset"] = "DateTimeOffset",
        ["decimal"] = "Decimal",
        ["float"] = "Float",
        ["geography"] = "Geography",
        ["geometry"] = "Geometry",
        ["hierarchyid"] = "HierarchyId",
        ["image"] = "Image",
        ["int"] = "Int",
        ["money"] = "Money",
        ["nchar"] = "NChar",
        ["ntext"] = "NText",
        ["numeric"] = "Numeric",
        ["nvarchar"] = "NVarChar",
        ["real"] = "Real",
        ["smalldatetime"] = "SmallDateTime",
        ["smallint"] = "SmallInt",
        ["smallmoney"] = "SmallMoney",
        ["sql_variant"] = "Variant",
        ["sysname"] = "SysName",
        ["text"] = "Text",
        ["time"] = "Time",
        ["timestamp"] = "Timestamp",
        ["tinyint"] = "TinyInt",
        ["uniqueidentifier"] = "UniqueIdentifier",
        ["varbinary"] = "VarBinary",
        ["varchar"] = "VarChar",
        ["xml"] = "Xml",
    };

    private static string MapSqlTypeName(string rawType)
    {
        if (SqlTypeMap.TryGetValue(rawType, out var mapped))
            return mapped;

        // Fallback: capitalize first letter of each word
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(rawType.ToLowerInvariant());
    }

    private static bool IsUnicodeType(string rawType)
    {
        return string.Equals(rawType, "nvarchar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawType, "nchar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawType, "ntext", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps sys.indexes.type_desc to SMO IndexType enum names.
    /// SMO uses: ClusteredIndex, NonClusteredIndex, ClusteredColumnStoreIndex, etc.
    /// sys.indexes uses: CLUSTERED, NONCLUSTERED, CLUSTERED COLUMNSTORE, NONCLUSTERED COLUMNSTORE, XML, SPATIAL, HEAP.
    /// </summary>
    private static string MapIndexType(string typeDesc)
    {
        return typeDesc.ToUpperInvariant() switch
        {
            "CLUSTERED" => "ClusteredIndex",
            "NONCLUSTERED" => "NonClusteredIndex",
            "CLUSTERED COLUMNSTORE" => "ClusteredColumnStoreIndex",
            "NONCLUSTERED COLUMNSTORE" => "NonClusteredColumnStoreIndex",
            "XML" => "SecondaryXmlIndex",
            "SPATIAL" => "SpatialIndex",
            "HEAP" => "HeapIndex",
            _ => typeDesc,
        };
    }

    /// <summary>
    /// Maps sys.foreign_keys referential action descriptions to SMO ForeignKeyAction names.
    /// SMO: NoAction, Cascade, SetNull, SetDefault.
    /// sys: NO_ACTION, CASCADE, SET_NULL, SET_DEFAULT.
    /// </summary>
    private static string MapReferentialAction(string actionDesc)
    {
        return actionDesc.ToUpperInvariant() switch
        {
            "NO_ACTION" => "NoAction",
            "CASCADE" => "Cascade",
            "SET_NULL" => "SetNull",
            "SET_DEFAULT" => "SetDefault",
            _ => actionDesc,
        };
    }

    #endregion

    #region Comparers

    /// <summary>
    /// Case-insensitive comparer for (schema, table) tuples.
    /// </summary>
    private sealed class SchemaTableComparer : IEqualityComparer<(string, string)>
    {
        public static readonly SchemaTableComparer Instance = new();
        public bool Equals((string, string) x, (string, string) y)
            => string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) obj)
        {
            var h = new HashCode();
            h.Add(obj.Item1, StringComparer.OrdinalIgnoreCase);
            h.Add(obj.Item2, StringComparer.OrdinalIgnoreCase);
            return h.ToHashCode();
        }
    }

    /// <summary>
    /// Case-insensitive comparer for (schema, table, name) tuples.
    /// </summary>
    private sealed class StringTuple3Comparer : IEqualityComparer<(string, string, string)>
    {
        public static readonly StringTuple3Comparer Instance = new();
        public bool Equals((string, string, string) x, (string, string, string) y)
            => string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Item3, y.Item3, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string, string) obj)
        {
            var h = new HashCode();
            h.Add(obj.Item1, StringComparer.OrdinalIgnoreCase);
            h.Add(obj.Item2, StringComparer.OrdinalIgnoreCase);
            h.Add(obj.Item3, StringComparer.OrdinalIgnoreCase);
            return h.ToHashCode();
        }
    }

    #endregion
}
