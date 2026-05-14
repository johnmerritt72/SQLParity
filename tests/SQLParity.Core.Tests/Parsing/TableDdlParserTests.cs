using System.Collections.Generic;
using SQLParity.Core.Parsing;
using Xunit;

namespace SQLParity.Core.Tests.Parsing;

public class TableDdlParserTests
{
    private static TableDdlParser MakeParser() => new TableDdlParser();

    [Fact]
    public void Parse_returns_null_and_warning_on_syntax_error()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            "THIS IS NOT VALID T-SQL %%%",
            "dbo", "Broken", sourceDatabase: null,
            out IReadOnlyList<string> warnings);

        Assert.Null(result);
        Assert.NotEmpty(warnings);
        Assert.Contains("Could not parse", warnings[0]);
    }

    [Fact]
    public void Parse_returns_null_and_warning_when_no_create_table_present()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            "SELECT 1",
            "dbo", "NoTable", sourceDatabase: null,
            out IReadOnlyList<string> warnings);

        Assert.Null(result);
        Assert.NotEmpty(warnings);
        Assert.Contains("No CREATE TABLE statement", warnings[0]);
    }

    [Fact]
    public void Parse_maps_columns_with_basic_types_and_nullability()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            @"CREATE TABLE dbo.T (
                Id INT NOT NULL,
                Name NVARCHAR(50) NULL,
                Created DATETIME NOT NULL,
                Big VARCHAR(MAX) NULL,
                Money DECIMAL(18, 2) NOT NULL
            )",
            "dbo", "T", null, out _);

        Assert.NotNull(result);
        Assert.Equal(5, result!.Columns.Count);

        Assert.Equal("Id", result.Columns[0].Name);
        Assert.Equal("int", result.Columns[0].DataType);
        Assert.False(result.Columns[0].IsNullable);
        Assert.Equal(0, result.Columns[0].OrdinalPosition);

        Assert.Equal("Name", result.Columns[1].Name);
        Assert.Equal("nvarchar", result.Columns[1].DataType);
        Assert.Equal(50, result.Columns[1].MaxLength);
        Assert.True(result.Columns[1].IsNullable);

        Assert.Equal("Big", result.Columns[3].Name);
        Assert.Equal("varchar", result.Columns[3].DataType);
        Assert.Equal(-1, result.Columns[3].MaxLength); // varchar(max)

        Assert.Equal("Money", result.Columns[4].Name);
        Assert.Equal("decimal", result.Columns[4].DataType);
        Assert.Equal(18, result.Columns[4].Precision);
        Assert.Equal(2, result.Columns[4].Scale);
    }

    [Fact]
    public void Parse_maps_identity_columns()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            "CREATE TABLE dbo.T (Id INT IDENTITY(1,1) NOT NULL)",
            "dbo", "T", null, out _);

        Assert.NotNull(result);
        Assert.True(result!.Columns[0].IsIdentity);
        Assert.Equal(1, result.Columns[0].IdentitySeed);
        Assert.Equal(1, result.Columns[0].IdentityIncrement);
    }

    [Fact]
    public void Parse_maps_default_constraint_with_round_tripped_expression()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            @"CREATE TABLE dbo.T (
                IsActive BIT NOT NULL DEFAULT 1,
                Created DATETIME NOT NULL DEFAULT GETUTCDATE()
            )",
            "dbo", "T", null, out _);

        Assert.NotNull(result);
        Assert.NotNull(result!.Columns[0].DefaultConstraint);
        Assert.Equal("1", result.Columns[0].DefaultConstraint!.Definition);
        Assert.NotNull(result.Columns[1].DefaultConstraint);
        Assert.Equal("getutcdate()", result.Columns[1].DefaultConstraint!.Definition.ToLowerInvariant());
    }

    [Fact]
    public void Parse_maps_named_default_constraint()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            "CREATE TABLE dbo.T (IsActive BIT NOT NULL CONSTRAINT DF_T_IsActive DEFAULT 1)",
            "dbo", "T", null, out _);

        Assert.NotNull(result);
        Assert.NotNull(result!.Columns[0].DefaultConstraint);
        Assert.Equal("DF_T_IsActive", result.Columns[0].DefaultConstraint!.Name);
    }

    [Fact]
    public void Parse_maps_computed_column()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            "CREATE TABLE dbo.T (Id INT NOT NULL, Doubled AS Id * 2 PERSISTED)",
            "dbo", "T", null, out _);

        Assert.NotNull(result);
        Assert.True(result!.Columns[1].IsComputed);
        Assert.True(result.Columns[1].IsPersisted);
        Assert.NotNull(result.Columns[1].ComputedText);
    }

    [Fact]
    public void Parse_maps_collation()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            "CREATE TABLE dbo.T (Name VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL)",
            "dbo", "T", null, out _);

        Assert.NotNull(result);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", result!.Columns[0].Collation);
    }

    [Fact]
    public void Parse_returns_table_model_with_correct_identity_fields()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            "CREATE TABLE dbo.T (Id INT NOT NULL)",
            "schema_X", "Name_Y", "Db_Z", out _);

        Assert.NotNull(result);
        // Use the schema/name from the args, not from CREATE TABLE — caller's metadata wins
        // (the parser's job is to populate Columns/Indexes/etc., not to override the Id).
        Assert.Equal("schema_X", result!.Schema);
        Assert.Equal("Name_Y", result.Name);
    }
}
