using SQLParity.Core.Model;
using SQLParity.Core.Parsing;
using Xunit;

namespace SQLParity.Core.Tests.Parsing;

public class SqlFileParserTests
{
    private static SqlFileParser Parser => new();

    [Fact]
    public void EmptyInput_NoObjects()
    {
        var result = Parser.Parse(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void NullInput_NoObjects()
    {
        var result = Parser.Parse(null!);
        Assert.Empty(result);
    }

    [Fact]
    public void WhitespaceOnly_NoObjects()
    {
        var result = Parser.Parse("   \n\t\n   ");
        Assert.Empty(result);
    }

    [Fact]
    public void OnlyComments_NoObjects()
    {
        var result = Parser.Parse("-- header\n/* block */\n");
        Assert.Empty(result);
    }

    [Fact]
    public void CreateProc_ShortKeyword_Recognized()
    {
        var result = Parser.Parse("CREATE PROC dbo.GetOrders AS SELECT 1");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.StoredProcedure, obj.ObjectType);
        Assert.Equal("dbo", obj.Id.Schema);
        Assert.Equal("GetOrders", obj.Id.Name);
        Assert.False(obj.IsCreateOrAlter);
        Assert.Equal(0, obj.BatchIndex);
    }

    [Fact]
    public void CreateProcedure_LongKeyword_Recognized()
    {
        var result = Parser.Parse("CREATE PROCEDURE dbo.GetOrders AS SELECT 1");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.StoredProcedure, obj.ObjectType);
    }

    [Fact]
    public void CreateOrAlter_FlagSet()
    {
        var result = Parser.Parse("CREATE OR ALTER PROC dbo.GetOrders AS SELECT 1");
        var obj = Assert.Single(result);
        Assert.True(obj.IsCreateOrAlter);
    }

    [Fact]
    public void CreateFunction_Recognized()
    {
        var result = Parser.Parse("CREATE FUNCTION dbo.Calc(@x INT) RETURNS INT AS BEGIN RETURN @x END");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.UserDefinedFunction, obj.ObjectType);
        Assert.Equal("Calc", obj.Id.Name);
    }

    [Fact]
    public void CreateView_Recognized()
    {
        var result = Parser.Parse("CREATE VIEW dbo.OrderSummary AS SELECT 1");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.View, obj.ObjectType);
    }

    [Fact]
    public void CreateTable_Recognized()
    {
        var result = Parser.Parse("CREATE TABLE dbo.Orders (Id INT NOT NULL)");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.Table, obj.ObjectType);
    }

    [Fact]
    public void CreateTrigger_Recognized()
    {
        var result = Parser.Parse("CREATE TRIGGER dbo.Audit ON dbo.Orders AFTER INSERT AS BEGIN SELECT 1 END");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.Trigger, obj.ObjectType);
    }

    [Fact]
    public void CreateTypeFromBaseType_IsUserDefinedDataType()
    {
        var result = Parser.Parse("CREATE TYPE dbo.OrderId FROM INT NOT NULL");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.UserDefinedDataType, obj.ObjectType);
    }

    [Fact]
    public void CreateTypeAsTable_IsUserDefinedTableType()
    {
        var result = Parser.Parse("CREATE TYPE dbo.OrderRows AS TABLE (Id INT NOT NULL)");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.UserDefinedTableType, obj.ObjectType);
    }

    [Fact]
    public void CreateSequence_Recognized()
    {
        var result = Parser.Parse("CREATE SEQUENCE dbo.OrderSeq AS BIGINT START WITH 1 INCREMENT BY 1");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.Sequence, obj.ObjectType);
    }

    [Fact]
    public void CreateSynonym_Recognized()
    {
        var result = Parser.Parse("CREATE SYNONYM dbo.Legacy FOR dbo.Orders");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.Synonym, obj.ObjectType);
    }

    [Fact]
    public void CreateSchema_Recognized()
    {
        var result = Parser.Parse("CREATE SCHEMA audit");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.Schema, obj.ObjectType);
        Assert.Equal("audit", obj.Id.Name);
    }

    [Fact]
    public void UnqualifiedName_DefaultsToDboSchema()
    {
        var result = Parser.Parse("CREATE PROC GetOrders AS SELECT 1");
        var obj = Assert.Single(result);
        Assert.Equal("dbo", obj.Id.Schema);
        Assert.Equal("GetOrders", obj.Id.Name);
    }

    [Fact]
    public void BracketedSchemaQualifiedName_StripsBrackets()
    {
        var result = Parser.Parse("CREATE PROC [dbo].[GetOrders] AS SELECT 1");
        var obj = Assert.Single(result);
        Assert.Equal("dbo", obj.Id.Schema);
        Assert.Equal("GetOrders", obj.Id.Name);
    }

    [Fact]
    public void BracketedNameWithSpaces_PreservedAsIs()
    {
        var result = Parser.Parse("CREATE TABLE [dbo].[Order Details] (Id INT)");
        var obj = Assert.Single(result);
        Assert.Equal("Order Details", obj.Id.Name);
    }

    [Fact]
    public void BracketedNameWithEscapedBracket_Unescaped()
    {
        // [Some]]Name] is the T-SQL escape for the literal name "Some]Name"
        var result = Parser.Parse("CREATE PROC [dbo].[Some]]Name] AS SELECT 1");
        var obj = Assert.Single(result);
        Assert.Equal("Some]Name", obj.Id.Name);
    }

    [Fact]
    public void CaseInsensitiveKeywords_Recognized()
    {
        var result = Parser.Parse("create or alter procedure dbo.Foo as select 1");
        var obj = Assert.Single(result);
        Assert.Equal(ObjectType.StoredProcedure, obj.ObjectType);
        Assert.True(obj.IsCreateOrAlter);
    }

    [Fact]
    public void LeadingHeaderComments_PreservedInDdl()
    {
        const string sql =
            "-- =================================\n" +
            "-- Author: Roy\n" +
            "-- =================================\n" +
            "CREATE PROC dbo.Foo AS SELECT 1";
        var result = Parser.Parse(sql);
        var obj = Assert.Single(result);
        Assert.Contains("-- Author: Roy", obj.Ddl);
        Assert.Contains("CREATE PROC dbo.Foo", obj.Ddl);
    }

    [Fact]
    public void CreateInsideLineComment_NotDetected()
    {
        const string sql = "-- CREATE PROC dbo.Fake AS SELECT 1\n-- another line";
        Assert.Empty(Parser.Parse(sql));
    }

    [Fact]
    public void CreateInsideBlockComment_NotDetected()
    {
        const string sql = "/* CREATE PROC dbo.Fake AS SELECT 1 */";
        Assert.Empty(Parser.Parse(sql));
    }

    [Fact]
    public void CreateInsideStringLiteral_NotDetected()
    {
        const string sql = "PRINT 'CREATE PROC dbo.Fake AS SELECT 1'";
        Assert.Empty(Parser.Parse(sql));
    }

    [Fact]
    public void GoSeparator_SplitsIntoMultipleBatches()
    {
        const string sql =
            "CREATE PROC dbo.A AS SELECT 1\n" +
            "GO\n" +
            "CREATE PROC dbo.B AS SELECT 2\n" +
            "GO\n";
        var result = Parser.Parse(sql);
        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].Id.Name);
        Assert.Equal(0, result[0].BatchIndex);
        Assert.Equal("B", result[1].Id.Name);
        Assert.Equal(1, result[1].BatchIndex);
    }

    [Fact]
    public void GoSeparator_CaseInsensitive()
    {
        const string sql = "CREATE PROC dbo.A AS SELECT 1\ngo\nCREATE PROC dbo.B AS SELECT 2";
        Assert.Equal(2, Parser.Parse(sql).Count);
    }

    [Fact]
    public void GoSeparator_TolleratesLeadingAndTrailingWhitespace()
    {
        const string sql = "CREATE PROC dbo.A AS SELECT 1\n  \tGO  \t\nCREATE PROC dbo.B AS SELECT 2";
        Assert.Equal(2, Parser.Parse(sql).Count);
    }

    [Fact]
    public void GoSeparator_WithBatchCount_Recognized()
    {
        // GO N means "run the previous batch N times" — for our purposes still a separator.
        const string sql = "CREATE PROC dbo.A AS SELECT 1\nGO 5\nCREATE PROC dbo.B AS SELECT 2";
        Assert.Equal(2, Parser.Parse(sql).Count);
    }

    [Fact]
    public void GoNotAtLineStart_NotASeparator()
    {
        // The "GO" inside "GOT" must not split the batch.
        const string sql = "CREATE PROC dbo.A AS SELECT 'GOT IT'";
        Assert.Single(Parser.Parse(sql));
    }

    [Fact]
    public void GoInsideStringLiteral_NotASeparator()
    {
        const string sql =
            "CREATE PROC dbo.A AS SELECT '\nGO\n'\n" +
            "GO\n" +
            "CREATE PROC dbo.B AS SELECT 1";
        var result = Parser.Parse(sql);
        Assert.Equal(2, result.Count);
        Assert.Contains("GO", result[0].Ddl);
    }

    [Fact]
    public void BatchWithoutCreate_Skipped()
    {
        const string sql =
            "USE [Foo];\n" +
            "GO\n" +
            "CREATE PROC dbo.A AS SELECT 1\n" +
            "GO\n";
        var result = Parser.Parse(sql);
        var obj = Assert.Single(result);
        Assert.Equal("A", obj.Id.Name);
        // BatchIndex reflects raw batch position, including the skipped USE batch.
        Assert.Equal(1, obj.BatchIndex);
    }

    [Fact]
    public void MultipleObjectsSameFile_AllReturned()
    {
        const string sql =
            "CREATE PROC dbo.A AS SELECT 1\n" +
            "GO\n" +
            "CREATE FUNCTION dbo.B() RETURNS INT AS BEGIN RETURN 1 END\n" +
            "GO\n" +
            "CREATE VIEW dbo.C AS SELECT 1\n";
        var result = Parser.Parse(sql);
        Assert.Equal(3, result.Count);
        Assert.Equal(ObjectType.StoredProcedure, result[0].ObjectType);
        Assert.Equal(ObjectType.UserDefinedFunction, result[1].ObjectType);
        Assert.Equal(ObjectType.View, result[2].ObjectType);
    }

    [Fact]
    public void BatchTextDoesNotIncludeTrailingGo()
    {
        const string sql = "CREATE PROC dbo.A AS SELECT 1\nGO\n";
        var obj = Assert.Single(Parser.Parse(sql));
        Assert.DoesNotContain("\nGO", obj.Ddl);
    }
}
