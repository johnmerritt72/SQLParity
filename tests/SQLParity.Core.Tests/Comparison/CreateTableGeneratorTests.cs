using System;
using System.Collections.Generic;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class CreateTableGeneratorTests
{
    private static ColumnModel Col(
        string name,
        string dataType = "int",
        int maxLen = 0,
        int precision = 0,
        int scale = 0,
        bool nullable = false,
        bool isIdentity = false,
        long identitySeed = 0,
        long identityIncrement = 0,
        bool isComputed = false,
        string? computedText = null,
        bool isPersisted = false,
        string? collation = null,
        DefaultConstraintModel? defaultConstraint = null,
        int ordinal = 0,
        string schema = "dbo",
        string table = "T") => new()
    {
        Id = SchemaQualifiedName.Child(schema, table, name),
        Name = name,
        DataType = dataType,
        MaxLength = maxLen,
        Precision = precision,
        Scale = scale,
        IsNullable = nullable,
        IsIdentity = isIdentity,
        IdentitySeed = identitySeed,
        IdentityIncrement = identityIncrement,
        IsComputed = isComputed,
        ComputedText = computedText,
        IsPersisted = isPersisted,
        Collation = collation,
        DefaultConstraint = defaultConstraint,
        OrdinalPosition = ordinal,
    };

    private static TableModel MakeTable(
        string schema,
        string name,
        IEnumerable<ColumnModel> columns,
        IEnumerable<IndexModel>? indexes = null,
        IEnumerable<CheckConstraintModel>? checks = null) => new()
    {
        Id = SchemaQualifiedName.TopLevel(schema, name),
        Schema = schema,
        Name = name,
        Ddl = string.Empty,
        Columns = new List<ColumnModel>(columns),
        Indexes = new List<IndexModel>(indexes ?? Array.Empty<IndexModel>()),
        ForeignKeys = Array.Empty<ForeignKeyModel>(),
        CheckConstraints = new List<CheckConstraintModel>(checks ?? Array.Empty<CheckConstraintModel>()),
        Triggers = Array.Empty<TriggerModel>(),
    };

    [Fact]
    public void TwoNonNullIntColumns_EmitsSimpleCreateTable()
    {
        var table = MakeTable("dbo", "T", new[]
        {
            Col("Id", "int", ordinal: 0),
            Col("Value", "int", ordinal: 1),
        });

        var ddl = CreateTableGenerator.Generate(table);

        Assert.Equal(
            "CREATE TABLE [dbo].[T](" + Environment.NewLine +
            "\t[Id] [int] NOT NULL," + Environment.NewLine +
            "\t[Value] [int] NOT NULL" + Environment.NewLine +
            ")",
            ddl);
    }
}
