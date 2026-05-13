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
    public void NoUseStatement_TargetDatabaseIsNull()
    {
        var obj = Assert.Single(Parser.Parse("CREATE PROC dbo.A AS SELECT 1"));
        Assert.Null(obj.TargetDatabase);
    }

    [Fact]
    public void SingleUse_BeforeCreate_TagsObject()
    {
        const string sql = "USE [Beta]\nGO\nCREATE PROC dbo.A AS SELECT 1\n";
        var (objects, warnings) = ParseWithWarnings(sql);
        var obj = Assert.Single(objects);
        Assert.Equal("Beta", obj.TargetDatabase);
        Assert.Empty(warnings);
    }

    [Fact]
    public void MultipleUseStatements_EachPairsWithItsOwnCreate_NoWarning()
    {
        const string sql =
            "USE [OrdersDb]\nGO\n" +
            "CREATE PROC dbo.GetOrders AS SELECT 1\nGO\n" +
            "USE [LookupDb]\nGO\n" +
            "CREATE PROC dbo.GetLookup AS SELECT 1\nGO\n";
        var (objects, warnings) = ParseWithWarnings(sql);
        Assert.Equal(2, objects.Count);
        Assert.Equal("OrdersDb", objects[0].TargetDatabase);
        Assert.Equal("LookupDb", objects[1].TargetDatabase);
        Assert.Empty(warnings);
    }

    [Fact]
    public void TwoUseStatementsBeforeOneCreate_LaterOneWins_WithWarning()
    {
        const string sql =
            "USE [OldDb]\nGO\n" +
            "USE [NewDb]\nGO\n" +
            "CREATE PROC dbo.A AS SELECT 1\nGO\n";
        var (objects, warnings) = ParseWithWarnings(sql);
        var obj = Assert.Single(objects);
        Assert.Equal("NewDb", obj.TargetDatabase);
        var warning = Assert.Single(warnings);
        Assert.Contains("OldDb", warning);
        Assert.Contains("NewDb", warning);
    }

    [Fact]
    public void RedundantUseSameDatabase_NoWarning()
    {
        const string sql =
            "USE [Beta]\nGO\n" +
            "USE [Beta]\nGO\n" +
            "CREATE PROC dbo.A AS SELECT 1\n";
        var (objects, warnings) = ParseWithWarnings(sql);
        Assert.Equal("Beta", Assert.Single(objects).TargetDatabase);
        Assert.Empty(warnings);
    }

    [Fact]
    public void UseStatement_BracketedAndUnbracketed_BothRecognized()
    {
        Assert.Equal("Beta", Assert.Single(Parser.Parse("USE [Beta]\nGO\nCREATE PROC dbo.A AS SELECT 1")).TargetDatabase);
        Assert.Equal("Beta", Assert.Single(Parser.Parse("USE Beta\nGO\nCREATE PROC dbo.A AS SELECT 1")).TargetDatabase);
        Assert.Equal("Beta", Assert.Single(Parser.Parse("USE [Beta];\nGO\nCREATE PROC dbo.A AS SELECT 1")).TargetDatabase);
    }

    [Fact]
    public void UseStatement_OnSameLineAsOtherStatement_IsRecognized()
    {
        // SSMS-generated scripts often wrap a USE between RAISERROR / PRINT
        // preambles. As long as USE starts its own line, the line-based
        // scan should pick it up. (The artificial single-line "USE [Foo]
        // CREATE PROC …" would actually fail to compile in T-SQL, since
        // CREATE PROC must be the first statement in its batch — so the
        // "lenient" reading is fine for any valid input.)
        const string sql =
            "RAISERROR('  - USE [Security] ...', 0, 1) WITH NOWAIT;\n" +
            "USE [Security]\n" +
            "GO\n" +
            "CREATE PROC dbo.A AS SELECT 1";
        var (objects, warnings) = ParseWithWarnings(sql);
        var obj = Assert.Single(objects);
        Assert.Equal("Security", obj.TargetDatabase);
        Assert.Empty(warnings);
    }

    [Fact]
    public void UseStatement_TrailingComment_StillRecognized()
    {
        const string sql = "USE [Beta] -- switch DBs\nGO\nCREATE PROC dbo.A AS SELECT 1";
        Assert.Equal("Beta", Assert.Single(Parser.Parse(sql)).TargetDatabase);
    }

    [Fact]
    public void RealWorldSsmsScript_WithRaiserrorPreamble_RoutesUseCorrectly()
    {
        // Reconstructed from a user-reported file that had RAISERROR / PRINT
        // logging mixed in with USE in the same batch. The parser previously
        // required a "pure" USE batch and silently dropped the routing,
        // sending [Security] procs to the default DB — where they didn't
        // exist, so they showed as DROP DESTRUCTIVE.
        const string sql =
            "RAISERROR('Script ID: …', 0, 1) WITH NOWAIT;\nGO\n\n" +
            "DECLARE @ScriptEnv nvarchar(15) = CONVERT(nvarchar, CONNECTIONPROPERTY('local_net_address'))\n" +
            "RAISERROR('Script Environment: %s (%s)', 0, 1, @ScriptEnv, @@servername) WITH NOWAIT;\nGO\n\n" +
            "RAISERROR('  - USE Security ...', 0, 1) WITH NOWAIT;\n" +
            "USE [Security]\nGO\n\n" +
            "/****** Object: StoredProcedure [Security].[Permission_List] ******/\n" +
            "SET ANSI_NULLS ON\nGO\n" +
            "SET QUOTED_IDENTIFIER ON\nGO\n\n" +
            "CREATE OR ALTER PROC [Security].[Permission_List] AS BEGIN SELECT 1 END\nGO\n";
        var (objects, warnings) = ParseWithWarnings(sql);
        var obj = Assert.Single(objects);
        Assert.Equal("Security", obj.TargetDatabase);
        Assert.Equal("Permission_List", obj.Id.Name);
        Assert.Empty(warnings);
    }

    [Fact]
    public void OverruledUseAcrossThreeUses_OnlyLastWins_WarningListsAllOverruled()
    {
        const string sql =
            "USE [DbA]\nGO\n" +
            "USE [DbB]\nGO\n" +
            "USE [DbC]\nGO\n" +
            "CREATE PROC dbo.A AS SELECT 1\n";
        var (objects, warnings) = ParseWithWarnings(sql);
        Assert.Equal("DbC", Assert.Single(objects).TargetDatabase);
        var w = Assert.Single(warnings);
        Assert.Contains("DbA", w);
        Assert.Contains("DbB", w);
    }

    private static (IReadOnlyList<ParsedSqlObject> Objects, IReadOnlyList<string> Warnings) ParseWithWarnings(string sql)
    {
        var objects = Parser.Parse(sql, out var warnings);
        return (objects, warnings);
    }

    [Fact]
    public void BatchTextDoesNotIncludeTrailingGo()
    {
        const string sql = "CREATE PROC dbo.A AS SELECT 1\nGO\n";
        var obj = Assert.Single(Parser.Parse(sql));
        Assert.DoesNotContain("\nGO", obj.Ddl);
    }
}
