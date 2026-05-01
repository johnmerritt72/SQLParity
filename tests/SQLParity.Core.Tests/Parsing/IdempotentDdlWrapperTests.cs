using SQLParity.Core.Model;
using SQLParity.Core.Parsing;
using Xunit;

namespace SQLParity.Core.Tests.Parsing;

public class IdempotentDdlWrapperTests
{
    [Fact]
    public void Procedure_PlainCreate_BecomesCreateOrAlter()
    {
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.StoredProcedure, "dbo", "GetOrders",
            "CREATE PROCEDURE [dbo].[GetOrders] AS SELECT 1");

        Assert.Contains("CREATE OR ALTER PROCEDURE", output);
        Assert.Contains("[dbo].[GetOrders]", output);
    }

    [Fact]
    public void Procedure_AlreadyCreateOrAlter_LeftAsIs()
    {
        const string input = "CREATE OR ALTER PROCEDURE [dbo].[GetOrders] AS SELECT 1";
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.StoredProcedure, "dbo", "GetOrders", input);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Procedure_HeaderCommentsAboveCreate_PreservedVerbatim()
    {
        const string input =
            "-- ===========================\n" +
            "-- Author: Roy\n" +
            "-- ===========================\n" +
            "CREATE PROCEDURE [dbo].[GetOrders] AS SELECT 1";
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.StoredProcedure, "dbo", "GetOrders", input);

        Assert.Contains("-- Author: Roy", output);
        Assert.Contains("-- ===========================", output);
        Assert.Contains("CREATE OR ALTER PROCEDURE", output);
        // Comments must come BEFORE the CREATE OR ALTER, not after.
        Assert.True(output.IndexOf("-- Author: Roy") < output.IndexOf("CREATE OR ALTER"));
    }

    [Fact]
    public void Function_PlainCreate_BecomesCreateOrAlter()
    {
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.UserDefinedFunction, "dbo", "Calc",
            "CREATE FUNCTION [dbo].[Calc]() RETURNS INT AS BEGIN RETURN 1 END");

        Assert.Contains("CREATE OR ALTER FUNCTION", output);
    }

    [Fact]
    public void View_PlainCreate_BecomesCreateOrAlter()
    {
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.View, "dbo", "OrderSummary",
            "CREATE VIEW [dbo].[OrderSummary] AS SELECT 1");

        Assert.Contains("CREATE OR ALTER VIEW", output);
    }

    [Fact]
    public void Trigger_PlainCreate_BecomesCreateOrAlter()
    {
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.Trigger, "dbo", "Audit",
            "CREATE TRIGGER [dbo].[Audit] ON [dbo].[Orders] AFTER INSERT AS BEGIN SELECT 1 END");

        Assert.Contains("CREATE OR ALTER TRIGGER", output);
    }

    [Fact]
    public void Table_WrappedInObjectIdNullGuard()
    {
        const string bare = "CREATE TABLE [dbo].[Orders] (Id INT NOT NULL)";
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.Table, "dbo", "Orders", bare);

        Assert.Contains("IF OBJECT_ID(N'[dbo].[Orders]', N'U') IS NULL", output);
        Assert.Contains("BEGIN", output);
        Assert.Contains("CREATE TABLE [dbo].[Orders]", output);
        Assert.Contains("END", output);
        // Critical: must NOT contain DROP — user explicitly forbade it.
        Assert.DoesNotContain("DROP", output);
    }

    [Fact]
    public void UserDefinedDataType_WrappedInSysTypesGuardWithoutTableTypeFlag()
    {
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.UserDefinedDataType, "dbo", "OrderId",
            "CREATE TYPE [dbo].[OrderId] FROM INT NOT NULL");

        Assert.Contains("FROM sys.types", output);
        Assert.Contains("SCHEMA_ID(N'dbo')", output);
        Assert.Contains("name = N'OrderId'", output);
        Assert.DoesNotContain("is_table_type", output);
        Assert.DoesNotContain("DROP", output);
    }

    [Fact]
    public void UserDefinedTableType_WrappedInSysTypesGuardWithTableTypeFlag()
    {
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.UserDefinedTableType, "dbo", "OrderRows",
            "CREATE TYPE [dbo].[OrderRows] AS TABLE (Id INT NOT NULL)");

        Assert.Contains("FROM sys.types", output);
        Assert.Contains("is_table_type = 1", output);
        Assert.DoesNotContain("DROP", output);
    }

    [Fact]
    public void Sequence_WrappedInSysSequencesGuard()
    {
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.Sequence, "dbo", "OrderSeq",
            "CREATE SEQUENCE [dbo].[OrderSeq] AS BIGINT START WITH 1 INCREMENT BY 1");

        Assert.Contains("FROM sys.sequences", output);
        Assert.Contains("SCHEMA_ID(N'dbo')", output);
        Assert.Contains("name = N'OrderSeq'", output);
        Assert.DoesNotContain("DROP", output);
    }

    [Fact]
    public void Synonym_WrappedInSysSynonymsGuard()
    {
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.Synonym, "dbo", "Legacy",
            "CREATE SYNONYM [dbo].[Legacy] FOR [dbo].[Orders]");

        Assert.Contains("FROM sys.synonyms", output);
        Assert.DoesNotContain("DROP", output);
    }

    [Fact]
    public void Schema_UsesIfNotExistsExecPattern()
    {
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.Schema, "audit", "audit",
            "CREATE SCHEMA [audit]");

        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'audit')", output);
        Assert.Contains("EXEC(N'CREATE SCHEMA [audit]')", output);
    }

    [Fact]
    public void NameWithEmbeddedQuote_EscapedInGuard()
    {
        // SQL identifiers can have a literal apostrophe (rare but legal in
        // [bracketed] form). Make sure we double it inside the N'...' literal.
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.Sequence, "dbo", "Bob's",
            "CREATE SEQUENCE [dbo].[Bob's] AS INT START WITH 1");

        Assert.Contains("name = N'Bob''s'", output);
    }

    [Fact]
    public void TableBareDdl_IndentedInsideBeginEnd()
    {
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.Table, "dbo", "Orders",
            "CREATE TABLE [dbo].[Orders] (\n    Id INT NOT NULL,\n    Total DECIMAL(10,2)\n)");

        // Each non-empty line of the original CREATE TABLE should be 4-space indented
        // inside BEGIN/END so the structure is readable.
        Assert.Contains("    CREATE TABLE [dbo].[Orders]", output);
        Assert.Contains("        Id INT NOT NULL,", output);
    }

    [Fact]
    public void UnknownObjectType_ReturnsBareDdlUnchanged()
    {
        // Index isn't routed through the folder writer in v1.2. The wrapper
        // shouldn't fail; it should just hand the bare DDL back.
        const string bare = "CREATE INDEX IX_Foo ON dbo.Bar (Id)";
        var output = IdempotentDdlWrapper.Wrap(ObjectType.Index, "dbo", "IX_Foo", bare);
        Assert.Equal(bare, output);
    }

    [Fact]
    public void Table_MultiBatchSmoOutput_ExtractsCreateBatchOnly()
    {
        // SMO scripts tables as multiple GO-separated batches: SET ANSI_NULLS,
        // SET QUOTED_IDENTIFIER, CREATE TABLE, then one ALTER TABLE per default
        // constraint. Wrapping the whole thing in IF OBJECT_ID … BEGIN … END
        // would put GOs inside a BEGIN/END block, which is malformed T-SQL.
        // The wrapper must extract just the CREATE TABLE batch.
        const string smo =
            "SET ANSI_NULLS ON\nGO\n" +
            "SET QUOTED_IDENTIFIER ON\nGO\n" +
            "CREATE TABLE [dbo].[Orders] (Id INT NOT NULL)\nGO\n" +
            "ALTER TABLE [dbo].[Orders] ADD CONSTRAINT DF_Orders_Total DEFAULT ((0)) FOR [Total]\nGO\n";

        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.Table, "dbo", "Orders", smo);

        Assert.Contains("CREATE TABLE [dbo].[Orders]", output);
        Assert.DoesNotContain("SET ANSI_NULLS", output);
        Assert.DoesNotContain("ALTER TABLE", output);
        Assert.DoesNotContain("DEFAULT ((0))", output);
        // The IF OBJECT_ID guard surrounds the CREATE.
        Assert.Contains("IF OBJECT_ID(N'[dbo].[Orders]', N'U') IS NULL", output);
    }

    [Fact]
    public void CreateInsideHeaderComment_DoesNotConfuseOrAlterRewrite()
    {
        // The leading CREATE inside a comment must not be rewritten — the real
        // CREATE comes after.
        const string input =
            "-- This file replaces the old CREATE PROC pattern.\n" +
            "CREATE PROCEDURE [dbo].[GetOrders] AS SELECT 1";
        var output = IdempotentDdlWrapper.Wrap(
            ObjectType.StoredProcedure, "dbo", "GetOrders", input);

        Assert.Contains("-- This file replaces the old CREATE PROC pattern.", output);
        // Only ONE "CREATE OR ALTER" should appear (on the real CREATE line).
        Assert.Equal(1, CountOccurrences(output, "CREATE OR ALTER"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
