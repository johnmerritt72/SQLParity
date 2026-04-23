using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using SQLParity.Core.Model;

namespace SQLParity.Core;

/// <summary>
/// Reads a SQL Server database's schema into an in-memory <see cref="DatabaseSchema"/> using SMO.
/// </summary>
public sealed class SchemaReader
{
    private readonly string _connectionString;
    private readonly string _databaseName;

    public SchemaReader(string connectionString, string databaseName)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _databaseName = databaseName ?? throw new ArgumentNullException(nameof(databaseName));
    }

    private static ScriptingOptions CreateScriptingOptions() => new ScriptingOptions
    {
        ScriptDrops = false,
        IncludeIfNotExists = false,
        SchemaQualify = true,
        AnsiPadding = true,
        NoCollation = false,
        IncludeHeaders = false,
        ScriptSchema = true,
        ScriptData = false,
        DriAllConstraints = true,
        Indexes = true,
        Triggers = true,
        ExtendedProperties = false,
        Permissions = false,
        IncludeDatabaseContext = false,
    };

    private static ScriptingOptions CreateTableScriptingOptions() => new ScriptingOptions
    {
        ScriptDrops = false,
        IncludeIfNotExists = false,
        SchemaQualify = true,
        AnsiPadding = false,
        // Preserve explicit COLLATE clauses on string columns. If the destination
        // database has a different default collation, stripping collations would
        // cause Msg 468 collation conflicts when procs compare strings across tables.
        NoCollation = false,
        IncludeHeaders = false,
        ScriptSchema = true,
        ScriptData = false,
        // Script inline constraints: PK, CHECK, DEFAULT — not foreign keys or indexes (scripted separately)
        DriPrimaryKey = true,
        DriChecks = true,
        DriDefaults = true,
        DriAllConstraints = false,
        DriAllKeys = false,
        DriIndexes = false,
        Indexes = false,
        Triggers = false,
        ExtendedProperties = false,
        Permissions = false,
        IncludeDatabaseContext = false,
    };

    private static string ScriptObject(StringCollection sc)
        => string.Join(Environment.NewLine, sc.Cast<string>());

    private static string ScriptTopLevel(StringCollection sc)
        => string.Join(Environment.NewLine + "GO" + Environment.NewLine, sc.Cast<string>());

    public DatabaseSchema ReadSchema()
    {
        return ReadSchema(null!, SchemaReadOptions.All);
    }

    public DatabaseSchema ReadSchema(IProgress<SchemaReadProgress>? progress)
    {
        return ReadSchema(progress, SchemaReadOptions.All);
    }

    /// <summary>
    /// Scripts a single table's DDL on demand. Used for lazy-loading — table DDL
    /// is not scripted during the initial read phase to save time on large databases.
    /// </summary>
    public string ScriptTable(string schema, string name)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connectionString);
            var serverConn = new ServerConnection
            {
                ServerInstance = builder.DataSource,
                DatabaseName = _databaseName,
                LoginSecure = builder.IntegratedSecurity,
                ConnectTimeout = builder.ConnectTimeout,
                StatementTimeout = 0,
            };
            if (!builder.IntegratedSecurity)
            {
                serverConn.Login = builder.UserID;
                serverConn.Password = builder.Password;
            }

            var server = new Server(serverConn);
            server.SetDefaultInitFields(typeof(Table), true);
            server.SetDefaultInitFields(typeof(Column), true);
            server.SetDefaultInitFields(typeof(Microsoft.SqlServer.Management.Smo.Index), true);
            server.SetDefaultInitFields(typeof(ForeignKey), true);
            server.SetDefaultInitFields(typeof(Check), true);

            var db = server.Databases[_databaseName];
            if (db == null) return $"-- Database '{_databaseName}' not found";

            var table = db.Tables[name, schema];
            if (table == null) return $"-- Table [{schema}].[{name}] not found";

            string ddl;
            try
            {
                var opts = CreateTableScriptingOptions();
                ddl = ScriptTopLevel(table.Script(opts));
            }
            catch
            {
                // Full scripting failed (e.g. VIEW DATABASE STATE denied).
                // Fall back to minimal options that avoid permission-heavy metadata queries.
                var minimal = new ScriptingOptions
                {
                    ScriptDrops = false,
                    SchemaQualify = true,
                    NoCollation = false,
                    DriPrimaryKey = true,
                    DriDefaults = true,
                    DriChecks = false,
                    DriAllConstraints = false,
                    DriAllKeys = false,
                    DriIndexes = false,
                    Indexes = false,
                    Triggers = false,
                    ExtendedProperties = false,
                    Permissions = false,
                };
                ddl = ScriptTopLevel(table.Script(minimal));
            }

            serverConn.Disconnect();
            return ddl;
        }
        catch (Exception ex)
        {
            // SMO wraps the real error in inner exceptions — unwrap to show the root cause
            var inner = ex;
            while (inner.InnerException != null)
                inner = inner.InnerException;
            var detail = inner == ex ? ex.Message : $"{ex.Message}\n-- Root cause: {inner.Message}";
            return $"-- Could not script table [{schema}].[{name}]: {detail}";
        }
    }

    public DatabaseSchema ReadSchema(IProgress<SchemaReadProgress>? progress, SchemaReadOptions options,
        System.Threading.CancellationToken cancellationToken = default)
    {
        options = options ?? SchemaReadOptions.All;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connectionString);
        var serverConn = new ServerConnection
        {
            ServerInstance = builder.DataSource,
            LoginSecure = builder.IntegratedSecurity,
            ConnectTimeout = builder.ConnectTimeout,
            // SMO's default StatementTimeout is 30s — too short for schema
            // enumeration on large, busy databases. Use 0 = no timeout.
            StatementTimeout = 0,
        };
        if (!builder.IntegratedSecurity)
        {
            serverConn.Login = builder.UserID;
            serverConn.Password = builder.Password;
        }

        progress?.Report(new SchemaReadProgress
        {
            CurrentOperation = "Connecting to server...",
            CompletedItems = 0,
            TotalItems = 0,
        });

        var server = new Server(serverConn);
        SetDefaultInitFields(server, options);

        progress?.Report(new SchemaReadProgress
        {
            CurrentOperation = "Opening database...",
            CompletedItems = 0,
            TotalItems = 0,
        });

        var db = server.Databases[_databaseName]
            ?? throw new InvalidOperationException($"Database '{_databaseName}' not found on server '{builder.DataSource}'.");

        // Attempt to prefetch table metadata in bulk (only if tables are included)
        if (options.IncludeTables)
        {
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = "Prefetching table metadata (this may take a while on large databases)...",
                CompletedItems = 0,
                TotalItems = 0,
            });
            try
            {
                db.PrefetchObjects(typeof(Table), new ScriptingOptions());
            }
            catch
            {
                // PrefetchObjects not available in this SMO version — continue without it
            }
        }

        progress?.Report(new SchemaReadProgress
        {
            CurrentOperation = "Counting objects...",
            CompletedItems = 0,
            TotalItems = 0,
        });

        // Quick count pass (metadata already pre-loaded, no scripting)
        int totalObjects = 0;
        if (options.IncludeSchemas)
            foreach (Schema s in db.Schemas) if (!s.IsSystemObject) totalObjects++;
        if (options.IncludeTables)
            foreach (Table t in db.Tables) if (!t.IsSystemObject) totalObjects++;
        if (options.IncludeViews)
            foreach (View v in db.Views) if (!v.IsSystemObject) totalObjects++;
        if (options.IncludeStoredProcedures)
            foreach (StoredProcedure sp in db.StoredProcedures) if (!sp.IsSystemObject) totalObjects++;
        if (options.IncludeFunctions)
            foreach (UserDefinedFunction f in db.UserDefinedFunctions) if (!f.IsSystemObject) totalObjects++;
        if (options.IncludeSequences)
            totalObjects += db.Sequences.Count;
        if (options.IncludeSynonyms)
            totalObjects += db.Synonyms.Count;
        if (options.IncludeUserDefinedDataTypes)
            totalObjects += db.UserDefinedDataTypes.Count;
        if (options.IncludeUserDefinedTableTypes)
            totalObjects += db.UserDefinedTableTypes.Count;

        int completed = 0;
        var perfLog = new SchemaReadPerformanceLog(_databaseName);

        // Create shared ScriptingOptions instances once (avoids re-allocation per Read* method)
        var opts = CreateScriptingOptions();
        var tableOpts = CreateTableScriptingOptions();

        var ct = cancellationToken;

        var sw = Stopwatch.StartNew();
        var schemas = options.IncludeSchemas
            ? ReadSchemas(db, opts, ref completed, totalObjects, progress)
            : new List<SchemaModel>();
        sw.Stop();
        perfLog.LogSection("Schemas", schemas.Count, sw.Elapsed);
        ct.ThrowIfCancellationRequested();

        sw.Restart();
        var tables = options.IncludeTables
            ? BulkTableMetadataReader.ReadAllTables(_connectionString, _databaseName, progress, ref completed, totalObjects, ct)
            : new List<TableModel>();
        sw.Stop();
        perfLog.LogSection("Tables", tables.Count, sw.Elapsed);
        ct.ThrowIfCancellationRequested();

        sw.Restart();
        var views = options.IncludeViews
            ? ReadViews(db, opts, ref completed, totalObjects, progress, ct)
            : new List<ViewModel>();
        sw.Stop();
        perfLog.LogSection("Views", views.Count, sw.Elapsed);
        ct.ThrowIfCancellationRequested();

        sw.Restart();
        var procs = options.IncludeStoredProcedures
            ? ReadStoredProcedures(db, opts, ref completed, totalObjects, progress, ct)
            : new List<StoredProcedureModel>();
        sw.Stop();
        perfLog.LogSection("StoredProcedures", procs.Count, sw.Elapsed);
        ct.ThrowIfCancellationRequested();

        sw.Restart();
        var functions = options.IncludeFunctions
            ? ReadFunctions(db, opts, ref completed, totalObjects, progress, ct)
            : new List<UserDefinedFunctionModel>();
        sw.Stop();
        perfLog.LogSection("Functions", functions.Count, sw.Elapsed);
        ct.ThrowIfCancellationRequested();

        sw.Restart();
        var sequences = options.IncludeSequences
            ? ReadSequences(db, opts, ref completed, totalObjects, progress)
            : new List<SequenceModel>();
        sw.Stop();
        perfLog.LogSection("Sequences", sequences.Count, sw.Elapsed);

        sw.Restart();
        var synonyms = options.IncludeSynonyms
            ? ReadSynonyms(db, opts, ref completed, totalObjects, progress)
            : new List<SynonymModel>();
        sw.Stop();
        perfLog.LogSection("Synonyms", synonyms.Count, sw.Elapsed);

        sw.Restart();
        var uddt = options.IncludeUserDefinedDataTypes
            ? ReadUserDefinedDataTypes(db, opts, ref completed, totalObjects, progress)
            : new List<UserDefinedDataTypeModel>();
        sw.Stop();
        perfLog.LogSection("UserDefinedDataTypes", uddt.Count, sw.Elapsed);

        sw.Restart();
        var udtt = options.IncludeUserDefinedTableTypes
            ? ReadUserDefinedTableTypes(db, opts, ref completed, totalObjects, progress)
            : new List<UserDefinedTableTypeModel>();
        sw.Stop();
        perfLog.LogSection("UserDefinedTableTypes", udtt.Count, sw.Elapsed);

        // Read external (cross-DB / linked-server) references via sys.sql_expression_dependencies.
        // Uses SQL Server's own dependency tracker — more reliable than text-parsing.
        progress?.Report(new SchemaReadProgress
        {
            CurrentOperation = "Scanning external references...",
            CompletedItems = completed,
            TotalItems = totalObjects,
        });
        var externalRefsRaw = ExternalReferenceReader.ReadAll(_connectionString);
        var externalRefs = new Dictionary<(string, string), IReadOnlyList<string>>();
        foreach (var kvp in externalRefsRaw)
            externalRefs[kvp.Key] = kvp.Value;

        perfLog.Finish();

        serverConn.Disconnect();

        return new DatabaseSchema
        {
            ServerName = builder.DataSource,
            DatabaseName = _databaseName,
            ReadAtUtc = DateTime.UtcNow,
            Schemas = schemas,
            Tables = tables,
            Views = views,
            StoredProcedures = procs,
            Functions = functions,
            Sequences = sequences,
            Synonyms = synonyms,
            UserDefinedDataTypes = uddt,
            UserDefinedTableTypes = udtt,
            ExternalReferences = externalRefs,
        };
    }

    private static void SetDefaultInitFields(Server server, SchemaReadOptions options)
    {
        // Only prefetch fields for object types we're actually going to read.
        // This avoids loading metadata for types the user has unchecked.
        if (options.IncludeSchemas)
            server.SetDefaultInitFields(typeof(Schema), true);

        if (options.IncludeTables)
        {
            server.SetDefaultInitFields(typeof(Table), true);
            server.SetDefaultInitFields(typeof(Column), true);
            server.SetDefaultInitFields(typeof(Microsoft.SqlServer.Management.Smo.Index), true);
            server.SetDefaultInitFields(typeof(IndexedColumn), true);
            server.SetDefaultInitFields(typeof(ForeignKey), true);
            server.SetDefaultInitFields(typeof(ForeignKeyColumn), true);
            server.SetDefaultInitFields(typeof(Check), true);
            server.SetDefaultInitFields(typeof(DefaultConstraint), true);
            server.SetDefaultInitFields(typeof(Trigger), true);
        }

        if (options.IncludeViews)
            server.SetDefaultInitFields(typeof(View), true);
        if (options.IncludeStoredProcedures)
            server.SetDefaultInitFields(typeof(StoredProcedure), true);
        if (options.IncludeFunctions)
            server.SetDefaultInitFields(typeof(UserDefinedFunction), true);
        if (options.IncludeSequences)
            server.SetDefaultInitFields(typeof(Sequence), true);
        if (options.IncludeSynonyms)
            server.SetDefaultInitFields(typeof(Synonym), true);
        if (options.IncludeUserDefinedDataTypes)
            server.SetDefaultInitFields(typeof(UserDefinedDataType), true);
        if (options.IncludeUserDefinedTableTypes)
            server.SetDefaultInitFields(typeof(UserDefinedTableType), true);
    }

    private static List<SchemaModel> ReadSchemas(Database db, ScriptingOptions opts, ref int completed, int total, IProgress<SchemaReadProgress>? progress)
    {
        var result = new List<SchemaModel>();

        foreach (Schema s in db.Schemas)
        {
            if (s.IsSystemObject)
                continue;

            completed++;
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = $"Schemas: [{s.Name}]",
                CompletedItems = completed,
                TotalItems = total,
            });

            string ddl;
            try
            {
                ddl = ScriptTopLevel(s.Script(opts));
            }
            catch
            {
                ddl = $"-- Could not script schema [{s.Name}]";
            }

            result.Add(new SchemaModel
            {
                Name = s.Name,
                Owner = s.Owner ?? string.Empty,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<TableModel> ReadTables(Database db, ScriptingOptions tableOpts, ref int completed, int total, IProgress<SchemaReadProgress>? progress, System.Threading.CancellationToken ct = default)
    {
        var result = new List<TableModel>();

        foreach (Table t in db.Tables)
        {
            ct.ThrowIfCancellationRequested();

            if (t.IsSystemObject)
                continue;

            completed++;
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = $"Tables: [{t.Schema}].[{t.Name}]",
                CompletedItems = completed,
                TotalItems = total,
            });

            // DDL is NOT scripted during the read phase — it's lazy-loaded on demand
            // when the user clicks a table in the results view. This saves ~200ms per table.
            // The comparison engine uses column/index/constraint metadata, not DDL text.

            var tableId = SchemaQualifiedName.TopLevel(t.Schema, t.Name);

            List<ColumnModel> columns;
            List<IndexModel> indexes;
            List<ForeignKeyModel> foreignKeys;
            List<CheckConstraintModel> checks;
            List<TriggerModel> triggers;
            try
            {
                columns = ReadColumns(t.Columns, t.Schema, t.Name);
                indexes = ReadIndexes(t.Indexes, t.Schema, t.Name);
                foreignKeys = ReadForeignKeys(t.ForeignKeys, t.Schema, t.Name);
                checks = ReadChecks(t.Checks, t.Schema, t.Name);
                triggers = ReadTriggers(t.Triggers, t.Schema, t.Name);
            }
            catch
            {
                columns = new List<ColumnModel>();
                indexes = new List<IndexModel>();
                foreignKeys = new List<ForeignKeyModel>();
                checks = new List<CheckConstraintModel>();
                triggers = new List<TriggerModel>();
            }

            result.Add(new TableModel
            {
                Id = tableId,
                Schema = t.Schema,
                Name = t.Name,
                Ddl = string.Empty,  // Lazy-loaded on demand via ScriptTable()
                Columns = columns,
                Indexes = indexes,
                ForeignKeys = foreignKeys,
                CheckConstraints = checks,
                Triggers = triggers,
            });
        }

        return result;
    }

    private static List<ColumnModel> ReadColumns(ColumnCollection columns, string tableSchema, string tableName)
    {
        var result = new List<ColumnModel>();

        foreach (Column col in columns)
        {
            DefaultConstraintModel? defaultConstraint = null;
            if (col.DefaultConstraint != null)
            {
                defaultConstraint = new DefaultConstraintModel
                {
                    Name = col.DefaultConstraint.Name,
                    Definition = col.DefaultConstraint.Text,
                };
            }

            result.Add(new ColumnModel
            {
                Id = SchemaQualifiedName.Child(tableSchema, tableName, col.Name),
                Name = col.Name,
                DataType = col.DataType.SqlDataType.ToString(),
                MaxLength = col.DataType.MaximumLength,
                Precision = col.DataType.NumericPrecision,
                Scale = col.DataType.NumericScale,
                IsNullable = col.Nullable,
                IsIdentity = col.Identity,
                IdentitySeed = col.Identity ? (long)col.IdentitySeed : 0L,
                IdentityIncrement = col.Identity ? (long)col.IdentityIncrement : 0L,
                IsComputed = col.Computed,
                ComputedText = col.Computed ? col.ComputedText : null,
                IsPersisted = col.IsPersisted,
                Collation = string.IsNullOrEmpty(col.Collation) ? null : col.Collation,
                DefaultConstraint = defaultConstraint,
                OrdinalPosition = col.ID - 1,
            });
        }

        return result;
    }

    private static List<IndexModel> ReadIndexes(IndexCollection indexes, string tableSchema, string tableName)
    {
        var result = new List<IndexModel>();

        foreach (Microsoft.SqlServer.Management.Smo.Index idx in indexes)
        {

            var cols = new List<IndexedColumnModel>();
            foreach (IndexedColumn ic in idx.IndexedColumns)
            {
                cols.Add(new IndexedColumnModel
                {
                    Name = ic.Name,
                    IsDescending = ic.Descending,
                    IsIncluded = ic.IsIncluded,
                });
            }

            result.Add(new IndexModel
            {
                Id = SchemaQualifiedName.Child(tableSchema, tableName, idx.Name),
                Name = idx.Name,
                IndexType = idx.IndexType.ToString(),
                IsClustered = idx.IsClustered,
                IsUnique = idx.IsUnique,
                IsPrimaryKey = idx.IndexKeyType == IndexKeyType.DriPrimaryKey,
                IsUniqueConstraint = idx.IndexKeyType == IndexKeyType.DriUniqueKey,
                HasFilter = idx.HasFilter,
                FilterDefinition = idx.HasFilter ? idx.FilterDefinition : null,
                Columns = cols,
                Ddl = string.Empty,
            });
        }

        return result;
    }

    private static List<ForeignKeyModel> ReadForeignKeys(ForeignKeyCollection foreignKeys, string tableSchema, string tableName)
    {
        var result = new List<ForeignKeyModel>();

        foreach (ForeignKey fk in foreignKeys)
        {
            var cols = new List<ForeignKeyColumnModel>();
            foreach (ForeignKeyColumn fkc in fk.Columns)
            {
                cols.Add(new ForeignKeyColumnModel
                {
                    LocalColumn = fkc.Name,
                    ReferencedColumn = fkc.ReferencedColumn,
                });
            }

            result.Add(new ForeignKeyModel
            {
                Id = SchemaQualifiedName.Child(tableSchema, tableName, fk.Name),
                Name = fk.Name,
                ReferencedTableSchema = fk.ReferencedTableSchema,
                ReferencedTableName = fk.ReferencedTable,
                DeleteAction = fk.DeleteAction.ToString(),
                UpdateAction = fk.UpdateAction.ToString(),
                IsEnabled = fk.IsEnabled,
                Columns = cols,
                Ddl = string.Empty,
            });
        }

        return result;
    }

    private static List<CheckConstraintModel> ReadChecks(CheckCollection checks, string tableSchema, string tableName)
    {
        var result = new List<CheckConstraintModel>();

        foreach (Check chk in checks)
        {
            result.Add(new CheckConstraintModel
            {
                Id = SchemaQualifiedName.Child(tableSchema, tableName, chk.Name),
                Name = chk.Name,
                Definition = chk.Text,
                IsEnabled = chk.IsEnabled,
                Ddl = string.Empty,
            });
        }

        return result;
    }

    private static List<TriggerModel> ReadTriggers(TriggerCollection triggers, string tableSchema, string tableName)
    {
        var result = new List<TriggerModel>();

        foreach (Trigger trig in triggers)
        {
            // Some SMO versions don't have IsSystemObject on Trigger; skip system-named triggers defensively
            bool isSystem = false;
            try
            {
                isSystem = trig.IsSystemObject;
            }
            catch
            {
                // If property doesn't exist, assume not system
            }

            if (isSystem)
                continue;

            string ddl;
            try
            {
                // Use TextHeader + TextBody directly — no Script() call
                ddl = (trig.TextHeader ?? "") + (trig.TextBody ?? "");
            }
            catch
            {
                ddl = $"-- Could not script trigger [{trig.Name}]";
            }

            result.Add(new TriggerModel
            {
                Id = SchemaQualifiedName.Child(tableSchema, tableName, trig.Name),
                Name = trig.Name,
                IsEnabled = trig.IsEnabled,
                FiresOnInsert = trig.Insert,
                FiresOnUpdate = trig.Update,
                FiresOnDelete = trig.Delete,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<ViewModel> ReadViews(Database db, ScriptingOptions opts, ref int completed, int total, IProgress<SchemaReadProgress>? progress, System.Threading.CancellationToken ct = default)
    {
        var result = new List<ViewModel>();

        foreach (View v in db.Views)
        {
            ct.ThrowIfCancellationRequested();

            if (v.IsSystemObject)
                continue;

            completed++;
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = $"Views: [{v.Schema}].[{v.Name}]",
                CompletedItems = completed,
                TotalItems = total,
            });

            string ddl;
            try
            {
                // Use TextHeader + TextBody directly — much faster than Script()
                // which triggers per-object server round-trips
                ddl = (v.TextHeader ?? "") + (v.TextBody ?? "");
                if (string.IsNullOrWhiteSpace(ddl))
                    ddl = ScriptTopLevel(v.Script(opts));
            }
            catch
            {
                ddl = $"-- Could not script view [{v.Schema}].[{v.Name}]";
            }

            result.Add(new ViewModel
            {
                Id = SchemaQualifiedName.TopLevel(v.Schema, v.Name),
                Schema = v.Schema,
                Name = v.Name,
                IsSchemaBound = v.IsSchemaBound,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<StoredProcedureModel> ReadStoredProcedures(Database db, ScriptingOptions opts, ref int completed, int total, IProgress<SchemaReadProgress>? progress, System.Threading.CancellationToken ct = default)
    {
        var result = new List<StoredProcedureModel>();

        foreach (StoredProcedure sp in db.StoredProcedures)
        {
            ct.ThrowIfCancellationRequested();

            if (sp.IsSystemObject)
                continue;

            completed++;
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = $"Stored Procedures: [{sp.Schema}].[{sp.Name}]",
                CompletedItems = completed,
                TotalItems = total,
            });

            string ddl;
            try
            {
                // Use TextHeader + TextBody directly — avoids Script() round-trip
                ddl = (sp.TextHeader ?? "") + (sp.TextBody ?? "");
                if (string.IsNullOrWhiteSpace(ddl))
                    ddl = ScriptTopLevel(sp.Script(opts));
            }
            catch
            {
                ddl = $"-- Could not script stored procedure [{sp.Schema}].[{sp.Name}]";
            }

            result.Add(new StoredProcedureModel
            {
                Id = SchemaQualifiedName.TopLevel(sp.Schema, sp.Name),
                Schema = sp.Schema,
                Name = sp.Name,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<UserDefinedFunctionModel> ReadFunctions(Database db, ScriptingOptions opts, ref int completed, int total, IProgress<SchemaReadProgress>? progress, System.Threading.CancellationToken ct = default)
    {
        var result = new List<UserDefinedFunctionModel>();

        foreach (UserDefinedFunction udf in db.UserDefinedFunctions)
        {
            ct.ThrowIfCancellationRequested();

            if (udf.IsSystemObject)
                continue;

            completed++;
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = $"Functions: [{udf.Schema}].[{udf.Name}]",
                CompletedItems = completed,
                TotalItems = total,
            });

            string ddl;
            try
            {
                // Use TextHeader + TextBody directly — avoids Script() round-trip
                ddl = (udf.TextHeader ?? "") + (udf.TextBody ?? "");
                if (string.IsNullOrWhiteSpace(ddl))
                    ddl = ScriptTopLevel(udf.Script(opts));
            }
            catch
            {
                ddl = $"-- Could not script function [{udf.Schema}].[{udf.Name}]";
            }

            var kind = udf.FunctionType switch
            {
                UserDefinedFunctionType.Scalar => FunctionKind.Scalar,
                UserDefinedFunctionType.Inline => FunctionKind.InlineTableValued,
                UserDefinedFunctionType.Table => FunctionKind.MultiStatementTableValued,
                _ => FunctionKind.Scalar,
            };

            result.Add(new UserDefinedFunctionModel
            {
                Id = SchemaQualifiedName.TopLevel(udf.Schema, udf.Name),
                Schema = udf.Schema,
                Name = udf.Name,
                Kind = kind,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<SequenceModel> ReadSequences(Database db, ScriptingOptions opts, ref int completed, int total, IProgress<SchemaReadProgress>? progress)
    {
        var result = new List<SequenceModel>();

        foreach (Sequence seq in db.Sequences)
        {
            completed++;
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = $"Sequences: [{seq.Schema}].[{seq.Name}]",
                CompletedItems = completed,
                TotalItems = total,
            });

            string ddl;
            try
            {
                ddl = ScriptTopLevel(seq.Script(opts));
            }
            catch
            {
                ddl = $"-- Could not script sequence [{seq.Schema}].[{seq.Name}]";
            }

            result.Add(new SequenceModel
            {
                Id = SchemaQualifiedName.TopLevel(seq.Schema, seq.Name),
                Schema = seq.Schema,
                Name = seq.Name,
                DataType = seq.DataType.SqlDataType.ToString(),
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<SynonymModel> ReadSynonyms(Database db, ScriptingOptions opts, ref int completed, int total, IProgress<SchemaReadProgress>? progress)
    {
        var result = new List<SynonymModel>();

        foreach (Synonym syn in db.Synonyms)
        {
            completed++;
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = $"Synonyms: [{syn.Schema}].[{syn.Name}]",
                CompletedItems = completed,
                TotalItems = total,
            });

            string ddl;
            try
            {
                ddl = ScriptTopLevel(syn.Script(opts));
            }
            catch
            {
                ddl = $"-- Could not script synonym [{syn.Schema}].[{syn.Name}]";
            }

            result.Add(new SynonymModel
            {
                Id = SchemaQualifiedName.TopLevel(syn.Schema, syn.Name),
                Schema = syn.Schema,
                Name = syn.Name,
                BaseObject = syn.BaseObject,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<UserDefinedDataTypeModel> ReadUserDefinedDataTypes(Database db, ScriptingOptions opts, ref int completed, int total, IProgress<SchemaReadProgress>? progress)
    {
        var result = new List<UserDefinedDataTypeModel>();

        foreach (UserDefinedDataType uddt in db.UserDefinedDataTypes)
        {
            completed++;
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = $"User-Defined Data Types: [{uddt.Schema}].[{uddt.Name}]",
                CompletedItems = completed,
                TotalItems = total,
            });

            string ddl;
            try
            {
                ddl = ScriptTopLevel(uddt.Script(opts));
            }
            catch
            {
                ddl = $"-- Could not script user-defined data type [{uddt.Schema}].[{uddt.Name}]";
            }

            result.Add(new UserDefinedDataTypeModel
            {
                Id = SchemaQualifiedName.TopLevel(uddt.Schema, uddt.Name),
                Schema = uddt.Schema,
                Name = uddt.Name,
                BaseType = uddt.SystemType,
                MaxLength = uddt.MaxLength,
                IsNullable = uddt.Nullable,
                Ddl = ddl,
            });
        }

        return result;
    }

    private static List<UserDefinedTableTypeModel> ReadUserDefinedTableTypes(Database db, ScriptingOptions opts, ref int completed, int total, IProgress<SchemaReadProgress>? progress)
    {
        var result = new List<UserDefinedTableTypeModel>();

        foreach (UserDefinedTableType udtt in db.UserDefinedTableTypes)
        {
            completed++;
            progress?.Report(new SchemaReadProgress
            {
                CurrentOperation = $"User-Defined Table Types: [{udtt.Schema}].[{udtt.Name}]",
                CompletedItems = completed,
                TotalItems = total,
            });

            string ddl;
            try
            {
                ddl = ScriptTopLevel(udtt.Script(opts));
            }
            catch
            {
                ddl = $"-- Could not script user-defined table type [{udtt.Schema}].[{udtt.Name}]";
            }

            var columns = ReadColumns(udtt.Columns, udtt.Schema, udtt.Name);

            result.Add(new UserDefinedTableTypeModel
            {
                Id = SchemaQualifiedName.TopLevel(udtt.Schema, udtt.Name),
                Schema = udtt.Schema,
                Name = udtt.Name,
                Columns = columns,
                Ddl = ddl,
            });
        }

        return result;
    }
}
