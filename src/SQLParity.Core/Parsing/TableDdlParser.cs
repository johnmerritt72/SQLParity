using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLParity.Core.Model;

namespace SQLParity.Core.Parsing;

/// <summary>
/// Parses a single CREATE TABLE batch (with optional IF OBJECT_ID idempotency
/// guard and sibling CREATE INDEX / ALTER TABLE ADD CONSTRAINT statements)
/// into a fully-populated <see cref="TableModel"/>. Returns null on parse
/// failure, with a warning describing the cause.
/// </summary>
public sealed class TableDdlParser
{
    public TableModel? Parse(
        string tableBatchDdl,
        string schema,
        string name,
        string? sourceDatabase,
        out IReadOnlyList<string> warnings)
    {
        var warningList = new List<string>();
        warnings = warningList;

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(tableBatchDdl ?? string.Empty);
        TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> errors);

        if (errors != null && errors.Count > 0)
        {
            var first = errors[0];
            warningList.Add(
                $"Could not parse table DDL: {first.Message} at line {first.Line}. " +
                "Falling back to text-only diff.");
            return null;
        }

        var createTable = FindFirstCreateTable(fragment);
        if (createTable == null)
        {
            warningList.Add("No CREATE TABLE statement found in batch. Falling back to text-only diff.");
            return null;
        }

        var id = SchemaQualifiedName.TopLevel(schema, name);
        var columns = MapColumns(createTable, schema, name);

        var indexes = new List<IndexModel>();
        var foreignKeys = new List<ForeignKeyModel>();
        var checkConstraints = new List<CheckConstraintModel>();

        // Inline column-level constraints (PK, FK, UNIQUE, CHECK on a single column).
        // DEFAULT lives on ColumnDefinition.DefaultConstraint and is handled in MapColumn;
        // nullability is also there. Everything else is in Constraints.
        if (createTable.Definition != null)
        {
            foreach (var col in createTable.Definition.ColumnDefinitions)
            {
                if (col.Constraints == null) continue;
                foreach (var c in col.Constraints)
                {
                    switch (c)
                    {
                        case UniqueConstraintDefinition uniq:
                            indexes.Add(MapUniqueAsIndex(uniq, schema, name,
                                singleColumnInline: col.ColumnIdentifier.Value));
                            break;
                        case ForeignKeyConstraintDefinition fk:
                            foreignKeys.Add(MapForeignKey(fk, schema, name,
                                singleColumnInline: col.ColumnIdentifier.Value));
                            break;
                        case CheckConstraintDefinition chk:
                            checkConstraints.Add(MapCheckConstraint(chk, schema, name));
                            break;
                    }
                }
            }

            // Table-level constraints (CONSTRAINT ... PRIMARY KEY/UNIQUE/CHECK/FOREIGN KEY (...)).
            if (createTable.Definition.TableConstraints != null)
            {
                foreach (var c in createTable.Definition.TableConstraints)
                {
                    switch (c)
                    {
                        case UniqueConstraintDefinition uniq:
                            indexes.Add(MapUniqueAsIndex(uniq, schema, name, singleColumnInline: null));
                            break;
                        case ForeignKeyConstraintDefinition fk:
                            foreignKeys.Add(MapForeignKey(fk, schema, name, singleColumnInline: null));
                            break;
                        case CheckConstraintDefinition chk:
                            checkConstraints.Add(MapCheckConstraint(chk, schema, name));
                            break;
                    }
                }
            }
        }

        return new TableModel
        {
            Id = id,
            Schema = schema,
            Name = name,
            Ddl = tableBatchDdl ?? string.Empty,
            Columns = columns,
            Indexes = indexes,
            ForeignKeys = foreignKeys,
            CheckConstraints = checkConstraints,
            Triggers = System.Array.Empty<TriggerModel>(),
        };
    }

    private static IReadOnlyList<ColumnModel> MapColumns(CreateTableStatement createTable, string schema, string tableName)
    {
        var result = new List<ColumnModel>();
        var defs = createTable.Definition?.ColumnDefinitions;
        if (defs == null) return result;

        for (int i = 0; i < defs.Count; i++)
        {
            var col = defs[i];
            result.Add(MapColumn(col, i, schema, tableName));
        }
        return result;
    }

    private static ColumnModel MapColumn(ColumnDefinition col, int ordinal, string schema, string tableName)
    {
        string colName = col.ColumnIdentifier.Value;

        // Computed column path
        if (col.ComputedColumnExpression != null)
        {
            return new ColumnModel
            {
                Id = SchemaQualifiedName.Child(schema, tableName, colName),
                Name = colName,
                DataType = string.Empty,
                MaxLength = 0,
                Precision = 0,
                Scale = 0,
                IsNullable = true,
                IsIdentity = false,
                IdentitySeed = 0,
                IdentityIncrement = 0,
                IsComputed = true,
                ComputedText = RoundTripExpression(col.ComputedColumnExpression),
                IsPersisted = col.IsPersisted,
                Collation = null,
                DefaultConstraint = null,
                OrdinalPosition = ordinal,
            };
        }

        string dataType = ExtractDataTypeName(col.DataType);
        (int maxLen, int precision, int scale) = ExtractDataTypeParameters(col.DataType);

        bool isNullable = true; // T-SQL default
        bool isIdentity = false;
        long identitySeed = 0;
        long identityIncrement = 0;
        string? collation = null;
        DefaultConstraintModel? defaultConstraint = null;

        // Per-column constraints (nullability lives in Constraints; default lives on the property)
        if (col.Constraints != null)
        {
            foreach (var c in col.Constraints)
            {
                if (c is NullableConstraintDefinition nc)
                    isNullable = nc.Nullable;
            }
        }

        // DEFAULT is a direct property on ColumnDefinition, not inside Constraints.
        if (col.DefaultConstraint != null)
        {
            var dc = col.DefaultConstraint;
            defaultConstraint = new DefaultConstraintModel
            {
                Name = dc.ConstraintIdentifier?.Value ?? string.Empty,
                Definition = RoundTripExpression(dc.Expression),
            };
        }

        // Identity / collation live on the column definition, not Constraints.
        if (col.IdentityOptions != null)
        {
            isIdentity = true;
            identitySeed = ExtractLongLiteral(col.IdentityOptions.IdentitySeed, defaultValue: 1);
            identityIncrement = ExtractLongLiteral(col.IdentityOptions.IdentityIncrement, defaultValue: 1);
        }
        if (col.Collation != null)
        {
            collation = col.Collation.Value;
        }

        return new ColumnModel
        {
            Id = SchemaQualifiedName.Child(schema, tableName, colName),
            Name = colName,
            DataType = dataType,
            MaxLength = maxLen,
            Precision = precision,
            Scale = scale,
            IsNullable = isNullable,
            IsIdentity = isIdentity,
            IdentitySeed = identitySeed,
            IdentityIncrement = identityIncrement,
            IsComputed = false,
            ComputedText = null,
            IsPersisted = false,
            Collation = collation,
            DefaultConstraint = defaultConstraint,
            OrdinalPosition = ordinal,
        };
    }

    private static string ExtractDataTypeName(DataTypeReference dt)
    {
        if (dt is SqlDataTypeReference sql)
            return sql.SqlDataTypeOption.ToString().ToLowerInvariant();
        if (dt is UserDataTypeReference user && user.Name != null)
            return user.Name.BaseIdentifier.Value.ToLowerInvariant();
        if (dt is ParameterizedDataTypeReference param && param.Name != null)
            return param.Name.BaseIdentifier.Value.ToLowerInvariant();
        return string.Empty;
    }

    private static (int MaxLen, int Precision, int Scale) ExtractDataTypeParameters(DataTypeReference dt)
    {
        if (dt is not ParameterizedDataTypeReference param || param.Parameters == null || param.Parameters.Count == 0)
            return (0, 0, 0);

        // varchar(max) / nvarchar(max) / varbinary(max) → MaxLength = -1
        if (dt is SqlDataTypeReference sqlDt)
        {
            switch (sqlDt.SqlDataTypeOption)
            {
                case SqlDataTypeOption.VarChar:
                case SqlDataTypeOption.NVarChar:
                case SqlDataTypeOption.VarBinary:
                case SqlDataTypeOption.Char:
                case SqlDataTypeOption.NChar:
                case SqlDataTypeOption.Binary:
                    if (param.Parameters[0] is MaxLiteral)
                        return (-1, 0, 0);
                    if (param.Parameters[0] is Literal lit && int.TryParse(lit.Value, out int len))
                        return (len, 0, 0);
                    return (0, 0, 0);

                case SqlDataTypeOption.Decimal:
                case SqlDataTypeOption.Numeric:
                {
                    int precision = 0, scale = 0;
                    if (param.Parameters.Count >= 1 && param.Parameters[0] is Literal p && int.TryParse(p.Value, out int pv))
                        precision = pv;
                    if (param.Parameters.Count >= 2 && param.Parameters[1] is Literal s && int.TryParse(s.Value, out int sv))
                        scale = sv;
                    return (0, precision, scale);
                }

                case SqlDataTypeOption.DateTime2:
                case SqlDataTypeOption.DateTimeOffset:
                case SqlDataTypeOption.Time:
                {
                    int scale = 0;
                    if (param.Parameters[0] is Literal s && int.TryParse(s.Value, out int sv))
                        scale = sv;
                    return (0, 0, scale);
                }
            }
        }
        return (0, 0, 0);
    }

    private static long ExtractLongLiteral(ScalarExpression? expr, long defaultValue)
    {
        if (expr is Literal literal)
        {
            if (long.TryParse(literal.Value, out long v)) return v;
        }
        return defaultValue;
    }

    private static string RoundTripExpression(TSqlFragment fragment)
    {
        var gen = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            SqlVersion = SqlVersion.Sql160,
            KeywordCasing = KeywordCasing.Lowercase,
            IncludeSemicolons = false,
            NewLineBeforeOpenParenthesisInMultilineList = false,
        });
        gen.GenerateScript(fragment, out string output);
        return (output ?? string.Empty).Trim();
    }

    private static IndexModel MapUniqueAsIndex(
        UniqueConstraintDefinition uniq,
        string schema,
        string tableName,
        string? singleColumnInline)
    {
        var cols = new List<IndexedColumnModel>();
        if (singleColumnInline != null)
        {
            cols.Add(new IndexedColumnModel
            {
                Name = singleColumnInline,
                IsDescending = false,
                IsIncluded = false,
            });
        }
        else if (uniq.Columns != null)
        {
            foreach (var c in uniq.Columns)
            {
                cols.Add(new IndexedColumnModel
                {
                    Name = c.Column.MultiPartIdentifier.Identifiers[^1].Value,
                    IsDescending = c.SortOrder == SortOrder.Descending,
                    IsIncluded = false,
                });
            }
        }

        bool clustered = uniq.Clustered.HasValue ? uniq.Clustered.Value : uniq.IsPrimaryKey;
        string indexName = uniq.ConstraintIdentifier?.Value ?? string.Empty;

        return new IndexModel
        {
            Id = SchemaQualifiedName.Child(schema, tableName, indexName),
            Name = indexName,
            IndexType = clustered ? "CLUSTERED" : "NONCLUSTERED",
            IsClustered = clustered,
            IsUnique = true,
            IsPrimaryKey = uniq.IsPrimaryKey,
            IsUniqueConstraint = !uniq.IsPrimaryKey,
            HasFilter = false,
            FilterDefinition = null,
            Columns = cols,
            Ddl = string.Empty,
        };
    }

    private static ForeignKeyModel MapForeignKey(
        ForeignKeyConstraintDefinition fk,
        string schema,
        string tableName,
        string? singleColumnInline)
    {
        var cols = new List<ForeignKeyColumnModel>();
        var refIds = fk.ReferenceTableName?.Identifiers;
        string refSchema = refIds != null && refIds.Count >= 2 ? refIds[refIds.Count - 2].Value : "dbo";
        string refTable = refIds != null && refIds.Count >= 1 ? refIds[refIds.Count - 1].Value : string.Empty;

        if (singleColumnInline != null)
        {
            string refCol = fk.ReferencedTableColumns != null && fk.ReferencedTableColumns.Count > 0
                ? fk.ReferencedTableColumns[0].Value
                : string.Empty;
            cols.Add(new ForeignKeyColumnModel
            {
                LocalColumn = singleColumnInline,
                ReferencedColumn = refCol,
            });
        }
        else
        {
            int n = System.Math.Min(
                fk.Columns?.Count ?? 0,
                fk.ReferencedTableColumns?.Count ?? 0);
            for (int i = 0; i < n; i++)
            {
                cols.Add(new ForeignKeyColumnModel
                {
                    LocalColumn = fk.Columns![i].Value,
                    ReferencedColumn = fk.ReferencedTableColumns![i].Value,
                });
            }
        }

        string fkName = fk.ConstraintIdentifier?.Value ?? string.Empty;
        return new ForeignKeyModel
        {
            Id = SchemaQualifiedName.Child(schema, tableName, fkName),
            Name = fkName,
            ReferencedTableSchema = refSchema,
            ReferencedTableName = refTable,
            DeleteAction = fk.DeleteAction.ToString().ToUpperInvariant(),
            UpdateAction = fk.UpdateAction.ToString().ToUpperInvariant(),
            IsEnabled = true,
            Columns = cols,
            Ddl = string.Empty,
        };
    }

    private static CheckConstraintModel MapCheckConstraint(
        CheckConstraintDefinition chk,
        string schema,
        string tableName)
    {
        string ckName = chk.ConstraintIdentifier?.Value ?? string.Empty;
        return new CheckConstraintModel
        {
            Id = SchemaQualifiedName.Child(schema, tableName, ckName),
            Name = ckName,
            Definition = RoundTripExpression(chk.CheckCondition),
            IsEnabled = true,
            Ddl = string.Empty,
        };
    }

    /// <summary>
    /// Depth-first walk that recurses into IfStatement.ThenStatement and
    /// BeginEndBlockStatement.StatementList so a CREATE TABLE inside an
    /// IF OBJECT_ID(...) IS NULL BEGIN ... END idempotency wrapper is found.
    /// </summary>
    private static CreateTableStatement? FindFirstCreateTable(TSqlFragment root)
    {
        var visitor = new CreateTableFinder();
        root.Accept(visitor);
        return visitor.Found;
    }

    private sealed class CreateTableFinder : TSqlFragmentVisitor
    {
        public CreateTableStatement? Found { get; private set; }

        public override void Visit(CreateTableStatement node)
        {
            if (Found == null) Found = node;
        }
    }
}
