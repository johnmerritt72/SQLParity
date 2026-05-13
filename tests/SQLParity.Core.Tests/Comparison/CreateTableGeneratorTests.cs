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

    [Fact]
    public void NullableColumn_EmitsNull()
    {
        var table = MakeTable("dbo", "T", new[] { Col("X", "int", nullable: true) });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("[X] [int] NULL", ddl);
        Assert.DoesNotContain("NOT NULL", ddl);
    }

    [Theory]
    [InlineData("varchar", 50, "[varchar](50)")]
    [InlineData("nvarchar", 100, "[nvarchar](100)")]
    [InlineData("char", 10, "[char](10)")]
    [InlineData("nchar", 5, "[nchar](5)")]
    [InlineData("binary", 16, "[binary](16)")]
    [InlineData("varbinary", 64, "[varbinary](64)")]
    [InlineData("varchar", -1, "[varchar](max)")]
    [InlineData("nvarchar", -1, "[nvarchar](max)")]
    [InlineData("varbinary", -1, "[varbinary](max)")]
    public void StringAndBinaryTypes_EmitLength(string dataType, int maxLen, string expected)
    {
        var table = MakeTable("dbo", "T", new[] { Col("X", dataType, maxLen: maxLen) });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("[X] " + expected, ddl);
    }

    [Theory]
    [InlineData("decimal", 18, 4, "[decimal](18, 4)")]
    [InlineData("numeric", 10, 2, "[numeric](10, 2)")]
    public void DecimalNumeric_EmitsPrecisionAndScale(string dataType, int precision, int scale, string expected)
    {
        var table = MakeTable("dbo", "T", new[] { Col("X", dataType, precision: precision, scale: scale) });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("[X] " + expected, ddl);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("bigint")]
    [InlineData("smallint")]
    [InlineData("tinyint")]
    [InlineData("bit")]
    [InlineData("datetime")]
    [InlineData("smalldatetime")]
    [InlineData("date")]
    [InlineData("money")]
    [InlineData("real")]
    [InlineData("float")]
    [InlineData("uniqueidentifier")]
    [InlineData("xml")]
    [InlineData("sql_variant")]
    [InlineData("sysname")]
    public void TypesWithoutSize_NoParens(string dataType)
    {
        var table = MakeTable("dbo", "T", new[] { Col("X", dataType) });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains($"[X] [{dataType}] NOT NULL", ddl);
        Assert.DoesNotContain($"[{dataType}](", ddl);
    }

    [Theory]
    [InlineData("datetime2", 7, "[datetime2](7)")]
    [InlineData("time", 3, "[time](3)")]
    [InlineData("datetimeoffset", 5, "[datetimeoffset](5)")]
    public void TemporalTypes_EmitPrecisionOnly(string dataType, int scale, string expected)
    {
        // sys.columns stores fractional-second precision in `scale` for these types
        var table = MakeTable("dbo", "T", new[] { Col("X", dataType, scale: scale) });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("[X] " + expected, ddl);
    }

    [Fact]
    public void IdentityColumn_EmitsIdentityClause()
    {
        var table = MakeTable("dbo", "T", new[]
        {
            Col("Id", "int", isIdentity: true, identitySeed: 1, identityIncrement: 1),
        });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("[Id] [int] IDENTITY(1,1) NOT NULL", ddl);
    }

    [Fact]
    public void IdentityColumn_NonDefaultSeedAndIncrement()
    {
        var table = MakeTable("dbo", "T", new[]
        {
            Col("Id", "bigint", isIdentity: true, identitySeed: 1000, identityIncrement: 5),
        });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("[Id] [bigint] IDENTITY(1000,5) NOT NULL", ddl);
    }

    [Fact]
    public void StringColumn_WithCollation_EmitsCollate()
    {
        var table = MakeTable("dbo", "T", new[]
        {
            Col("Name", "nvarchar", maxLen: 100, collation: "SQL_Latin1_General_CP1_CI_AS"),
        });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("[Name] [nvarchar](100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL", ddl);
    }

    [Fact]
    public void IntColumn_WithCollationSet_DoesNotEmitCollate()
    {
        // Defensive: catalog might return a non-null collation on a non-string column;
        // COLLATE on a non-character type is invalid SQL.
        var table = MakeTable("dbo", "T", new[]
        {
            Col("Id", "int", collation: "SQL_Latin1_General_CP1_CI_AS"),
        });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.DoesNotContain("COLLATE", ddl);
    }

    [Fact]
    public void DefaultConstraint_EmittedAfterNullSpec()
    {
        var def = new DefaultConstraintModel { Name = "DF_T_CreatedAt", Definition = "(getdate())" };
        var table = MakeTable("dbo", "T", new[]
        {
            Col("CreatedAt", "datetime", defaultConstraint: def),
        });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("[CreatedAt] [datetime] NOT NULL CONSTRAINT [DF_T_CreatedAt] DEFAULT (getdate())", ddl);
    }

    [Fact]
    public void DefaultConstraint_DefinitionAlreadyParenthesized_NotDoubleWrapped()
    {
        // sys.default_constraints.definition typically arrives already wrapped in (),
        // e.g. "((0))" for "DEFAULT 0". Don't add another layer.
        var def = new DefaultConstraintModel { Name = "DF_T_Count", Definition = "((0))" };
        var table = MakeTable("dbo", "T", new[]
        {
            Col("Count", "int", defaultConstraint: def),
        });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("CONSTRAINT [DF_T_Count] DEFAULT ((0))", ddl);
        Assert.DoesNotContain("DEFAULT (((0)))", ddl);
    }

    [Fact]
    public void ComputedColumn_NonPersisted_EmitsAsExpression()
    {
        var table = MakeTable("dbo", "T", new[]
        {
            Col("Total", "decimal", precision: 18, scale: 4,
                isComputed: true, computedText: "([Price]*[Qty])", isPersisted: false),
        });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("[Total] AS ([Price]*[Qty])", ddl);
        Assert.DoesNotContain("PERSISTED", ddl);
        Assert.DoesNotContain("[decimal]", ddl);     // No type when computed
        Assert.DoesNotContain("NOT NULL", ddl);      // No null spec when computed
    }

    [Fact]
    public void ComputedColumn_Persisted_AppendsPersistedKeyword()
    {
        var table = MakeTable("dbo", "T", new[]
        {
            Col("Total", "decimal", precision: 18, scale: 4,
                isComputed: true, computedText: "([Price]*[Qty])", isPersisted: true),
        });
        var ddl = CreateTableGenerator.Generate(table);
        Assert.Contains("[Total] AS ([Price]*[Qty]) PERSISTED", ddl);
    }
}
