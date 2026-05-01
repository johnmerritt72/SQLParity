using System;
using System.Collections.Generic;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class SchemaComparatorTests
{
    #region Helpers

    private static DatabaseSchema EmptySchema(string name = "TestDb") => new()
    {
        ServerName = "localhost",
        DatabaseName = name,
        ReadAtUtc = DateTime.UtcNow,
        Schemas = Array.Empty<SchemaModel>(),
        Tables = Array.Empty<TableModel>(),
        Views = Array.Empty<ViewModel>(),
        StoredProcedures = Array.Empty<StoredProcedureModel>(),
        Functions = Array.Empty<UserDefinedFunctionModel>(),
        Sequences = Array.Empty<SequenceModel>(),
        Synonyms = Array.Empty<SynonymModel>(),
        UserDefinedDataTypes = Array.Empty<UserDefinedDataTypeModel>(),
        UserDefinedTableTypes = Array.Empty<UserDefinedTableTypeModel>(),
    };

    private static TableModel MakeTable(
        string schema,
        string name,
        string ddl = "CREATE TABLE ...",
        IReadOnlyList<ColumnModel>? columns = null) => new()
    {
        Id = SchemaQualifiedName.TopLevel(schema, name),
        Schema = schema,
        Name = name,
        Ddl = ddl,
        Columns = columns ?? Array.Empty<ColumnModel>(),
        Indexes = Array.Empty<IndexModel>(),
        ForeignKeys = Array.Empty<ForeignKeyModel>(),
        CheckConstraints = Array.Empty<CheckConstraintModel>(),
        Triggers = Array.Empty<TriggerModel>(),
    };

    private static ViewModel MakeView(string schema, string name, string ddl = "CREATE VIEW ...") => new()
    {
        Id = SchemaQualifiedName.TopLevel(schema, name),
        Schema = schema,
        Name = name,
        IsSchemaBound = false,
        Ddl = ddl,
    };

    private static StoredProcedureModel MakeProc(string schema, string name, string ddl = "CREATE PROC ...") => new()
    {
        Id = SchemaQualifiedName.TopLevel(schema, name),
        Schema = schema,
        Name = name,
        Ddl = ddl,
    };

    private static UserDefinedFunctionModel MakeFunc(string schema, string name, string ddl = "CREATE FUNCTION ...") => new()
    {
        Id = SchemaQualifiedName.TopLevel(schema, name),
        Schema = schema,
        Name = name,
        Kind = FunctionKind.Scalar,
        Ddl = ddl,
    };

    private static DatabaseSchema WithFuncs(DatabaseSchema schema, params UserDefinedFunctionModel[] funcs)
    {
        return new DatabaseSchema
        {
            ServerName = schema.ServerName,
            DatabaseName = schema.DatabaseName,
            ReadAtUtc = schema.ReadAtUtc,
            Schemas = schema.Schemas,
            Tables = schema.Tables,
            Views = schema.Views,
            StoredProcedures = schema.StoredProcedures,
            Functions = funcs,
            Sequences = schema.Sequences,
            Synonyms = schema.Synonyms,
            UserDefinedDataTypes = schema.UserDefinedDataTypes,
            UserDefinedTableTypes = schema.UserDefinedTableTypes,
        };
    }

    private static ColumnModel MakeColumn(
        string table,
        string name,
        string dataType = "Int",
        int maxLen = 0,
        bool nullable = false) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", table, name),
        Name = name,
        DataType = dataType,
        MaxLength = maxLen,
        Precision = 0,
        Scale = 0,
        IsNullable = nullable,
        IsIdentity = false,
        IdentitySeed = 0,
        IdentityIncrement = 0,
        IsComputed = false,
        ComputedText = null,
        IsPersisted = false,
        Collation = null,
        DefaultConstraint = null,
        OrdinalPosition = 0,
    };

    private static DatabaseSchema WithTables(DatabaseSchema schema, params TableModel[] tables)
    {
        return new DatabaseSchema
        {
            ServerName = schema.ServerName,
            DatabaseName = schema.DatabaseName,
            ReadAtUtc = schema.ReadAtUtc,
            Schemas = schema.Schemas,
            Tables = tables,
            Views = schema.Views,
            StoredProcedures = schema.StoredProcedures,
            Functions = schema.Functions,
            Sequences = schema.Sequences,
            Synonyms = schema.Synonyms,
            UserDefinedDataTypes = schema.UserDefinedDataTypes,
            UserDefinedTableTypes = schema.UserDefinedTableTypes,
        };
    }

    private static DatabaseSchema WithViews(DatabaseSchema schema, params ViewModel[] views)
    {
        return new DatabaseSchema
        {
            ServerName = schema.ServerName,
            DatabaseName = schema.DatabaseName,
            ReadAtUtc = schema.ReadAtUtc,
            Schemas = schema.Schemas,
            Tables = schema.Tables,
            Views = views,
            StoredProcedures = schema.StoredProcedures,
            Functions = schema.Functions,
            Sequences = schema.Sequences,
            Synonyms = schema.Synonyms,
            UserDefinedDataTypes = schema.UserDefinedDataTypes,
            UserDefinedTableTypes = schema.UserDefinedTableTypes,
        };
    }

    private static DatabaseSchema WithProcs(DatabaseSchema schema, params StoredProcedureModel[] procs)
    {
        return new DatabaseSchema
        {
            ServerName = schema.ServerName,
            DatabaseName = schema.DatabaseName,
            ReadAtUtc = schema.ReadAtUtc,
            Schemas = schema.Schemas,
            Tables = schema.Tables,
            Views = schema.Views,
            StoredProcedures = procs,
            Functions = schema.Functions,
            Sequences = schema.Sequences,
            Synonyms = schema.Synonyms,
            UserDefinedDataTypes = schema.UserDefinedDataTypes,
            UserDefinedTableTypes = schema.UserDefinedTableTypes,
        };
    }

    #endregion

    [Fact]
    public void IdenticalSchemas_NoChanges()
    {
        var table = MakeTable("dbo", "Orders");
        var a = WithTables(EmptySchema("DbA"), table);
        var b = WithTables(EmptySchema("DbB"), table);

        var result = SchemaComparator.Compare(a, b);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void TableOnlyInSideA_IsNew()
    {
        var table = MakeTable("dbo", "Orders");
        var a = WithTables(EmptySchema("DbA"), table);
        var b = EmptySchema("DbB");

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.New, change.Status);
        Assert.Equal(ObjectType.Table, change.ObjectType);
        Assert.Equal(table.Id, change.Id);
        Assert.Equal(table.Ddl, change.DdlSideA);
        Assert.Null(change.DdlSideB);
    }

    [Fact]
    public void TableOnlyInSideB_IsDropped()
    {
        var table = MakeTable("dbo", "Orders");
        var a = EmptySchema("DbA");
        var b = WithTables(EmptySchema("DbB"), table);

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Dropped, change.Status);
        Assert.Equal(ObjectType.Table, change.ObjectType);
        Assert.Equal(table.Id, change.Id);
        Assert.Null(change.DdlSideA);
        Assert.Equal(table.Ddl, change.DdlSideB);
    }

    [Fact]
    public void TableWithDifferentDdl_IsModified()
    {
        var tableA = MakeTable("dbo", "Orders", ddl: "CREATE TABLE Orders (Id INT)");
        var tableB = MakeTable("dbo", "Orders", ddl: "CREATE TABLE Orders (Id BIGINT)");
        var a = WithTables(EmptySchema("DbA"), tableA);
        var b = WithTables(EmptySchema("DbB"), tableB);

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);
        Assert.Equal(ObjectType.Table, change.ObjectType);
    }

    [Fact]
    public void ComparesViewsBySchemaQualifiedName()
    {
        var viewA = MakeView("dbo", "OrderSummary", ddl: "CREATE VIEW OrderSummary AS SELECT 1");
        var viewB = MakeView("dbo", "OrderSummary", ddl: "CREATE VIEW OrderSummary AS SELECT 2");
        var a = WithViews(EmptySchema("DbA"), viewA);
        var b = WithViews(EmptySchema("DbB"), viewB);

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);
        Assert.Equal(ObjectType.View, change.ObjectType);
        Assert.Equal(viewA.Id, change.Id);
    }

    [Fact]
    public void ComparesProcsBySchemaQualifiedName()
    {
        var proc = MakeProc("dbo", "GetOrders");
        var a = WithProcs(EmptySchema("DbA"), proc);
        var b = EmptySchema("DbB");

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.New, change.Status);
        Assert.Equal(ObjectType.StoredProcedure, change.ObjectType);
        Assert.Equal(proc.Id, change.Id);
    }

    [Fact]
    public void MultipleObjectTypes_AllDetected()
    {
        var table = MakeTable("dbo", "Orders");
        var view = MakeView("dbo", "OrderSummary");
        var proc = MakeProc("dbo", "GetOrders");

        var a = new DatabaseSchema
        {
            ServerName = "localhost",
            DatabaseName = "DbA",
            ReadAtUtc = DateTime.UtcNow,
            Schemas = Array.Empty<SchemaModel>(),
            Tables = new[] { table },
            Views = new[] { view },
            StoredProcedures = new[] { proc },
            Functions = Array.Empty<UserDefinedFunctionModel>(),
            Sequences = Array.Empty<SequenceModel>(),
            Synonyms = Array.Empty<SynonymModel>(),
            UserDefinedDataTypes = Array.Empty<UserDefinedDataTypeModel>(),
            UserDefinedTableTypes = Array.Empty<UserDefinedTableTypeModel>(),
        };
        var b = EmptySchema("DbB");

        var result = SchemaComparator.Compare(a, b);

        Assert.Equal(3, result.Changes.Count);
        Assert.Contains(result.Changes, c => c.ObjectType == ObjectType.Table && c.Status == ChangeStatus.New);
        Assert.Contains(result.Changes, c => c.ObjectType == ObjectType.View && c.Status == ChangeStatus.New);
        Assert.Contains(result.Changes, c => c.ObjectType == ObjectType.StoredProcedure && c.Status == ChangeStatus.New);
    }

    [Fact]
    public void ComparisonResult_HasBothSchemas()
    {
        var a = EmptySchema("DbA");
        var b = EmptySchema("DbB");

        var result = SchemaComparator.Compare(a, b);

        Assert.Same(a, result.SideA);
        Assert.Same(b, result.SideB);
    }

    [Fact]
    public void ModifiedTable_IncludesColumnChanges()
    {
        var colShared = MakeColumn("Orders", "Id");
        var colExtra = MakeColumn("Orders", "ExtraCol");

        // SideA has two columns, SideB has one
        var tableA = MakeTable("dbo", "Orders", columns: new[] { colShared, colExtra });
        var tableB = MakeTable("dbo", "Orders", columns: new[] { colShared });

        var a = WithTables(EmptySchema("DbA"), tableA);
        var b = WithTables(EmptySchema("DbB"), tableB);

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);
        Assert.Equal(ObjectType.Table, change.ObjectType);

        var colChange = Assert.Single(change.ColumnChanges);
        Assert.Equal(ChangeStatus.New, colChange.Status);
        Assert.Equal("ExtraCol", colChange.ColumnName);
    }

    [Fact]
    public void DroppedTable_HasDestructiveRisk()
    {
        var table = MakeTable("dbo", "Orders");
        var a = EmptySchema("DbA");
        var b = WithTables(EmptySchema("DbB"), table);

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Dropped, change.Status);
        Assert.Equal(RiskTier.Destructive, change.Risk);
        Assert.NotEmpty(change.Reasons);
    }

    [Fact]
    public void NewTable_HasSafeRisk()
    {
        var table = MakeTable("dbo", "Orders");
        var a = WithTables(EmptySchema("DbA"), table);
        var b = EmptySchema("DbB");

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.New, change.Status);
        Assert.Equal(RiskTier.Safe, change.Risk);
        Assert.NotEmpty(change.Reasons);
    }

    [Fact]
    public void ModifiedTable_DroppedColumn_HasDestructiveRisk()
    {
        var colShared = MakeColumn("Orders", "Id");
        var colExtra = MakeColumn("Orders", "ExtraCol");

        // SideA has both columns, SideB only has the shared one (so ExtraCol is "new" = in A not B)
        var tableA = MakeTable("dbo", "Orders", columns: new[] { colShared, colExtra });
        var tableB = MakeTable("dbo", "Orders", columns: new[] { colShared });

        var a = WithTables(EmptySchema("DbA"), tableA);
        var b = WithTables(EmptySchema("DbB"), tableB);

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);

        // ExtraCol is New (in A not B), which maps to Safe per ColumnRiskClassifier.
        // But to test a truly Dropped column we need it in B not A.
        // Re-run with reversed sides for a proper Dropped column:
        var result2 = SchemaComparator.Compare(
            WithTables(EmptySchema("DbA"), tableB),
            WithTables(EmptySchema("DbB"), tableA));

        var change2 = Assert.Single(result2.Changes);
        Assert.Equal(ChangeStatus.Modified, change2.Status);
        var colChange = Assert.Single(change2.ColumnChanges);
        Assert.Equal(ChangeStatus.Dropped, colChange.Status);
        Assert.Equal(RiskTier.Destructive, colChange.Risk);
        Assert.Equal(RiskTier.Destructive, change2.Risk);
    }

    [Fact]
    public void DroppedTable_HasPreFlightQuery()
    {
        var table = MakeTable("dbo", "Orders");
        var a = EmptySchema("DbA");
        var b = WithTables(EmptySchema("DbB"), table);

        var result = SchemaComparator.Compare(a, b);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Dropped, change.Status);
        Assert.Equal(RiskTier.Destructive, change.Risk);
        Assert.NotNull(change.PreFlightSql);
        Assert.Contains("SELECT COUNT(*)", change.PreFlightSql);
    }

    [Fact]
    public void StoredProc_CommentOnlyDifference_StillModifiedByDefault()
    {
        var procA = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS\n-- updated 2026\nSELECT 1");
        var procB = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS\n-- updated 2025\nSELECT 1");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(a, b);

        Assert.Single(result.Changes);
    }

    [Fact]
    public void StoredProc_CommentOnlyDifference_SuppressedWhenIgnoreCommentsEnabled()
    {
        var procA = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS\n-- updated 2026\nSELECT 1");
        var procB = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS\n/* version 2 */\nSELECT 1");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(a, b, SchemaReadOptions.All, ignoreCommentsInStoredProcedures: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void StoredProc_RealCodeDifference_StillReportedWhenIgnoreCommentsEnabled()
    {
        var procA = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS\n-- comment\nSELECT 1");
        var procB = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS\n-- comment\nSELECT 2");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(a, b, SchemaReadOptions.All, ignoreCommentsInStoredProcedures: true);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);
    }

    [Fact]
    public void StoredProc_CommentLikeStringInsideLiteral_NotStripped()
    {
        var procA = MakeProc("dbo", "WriteLog",
            "CREATE PROC WriteLog AS SELECT '-- not a comment'");
        var procB = MakeProc("dbo", "WriteLog",
            "CREATE PROC WriteLog AS SELECT '-- different'");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(a, b, SchemaReadOptions.All, ignoreCommentsInStoredProcedures: true);

        Assert.Single(result.Changes);
    }

    [Fact]
    public void StoredProc_WhitespaceOnlyDifference_OutsideLiterals_SuppressedWhenOptionEnabled()
    {
        var procA = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS\n    SELECT     1,    2\n    FROM     dbo.Orders");
        var procB = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS SELECT 1, 2 FROM dbo.Orders");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreCommentsInStoredProcedures: false,
            ignoreWhitespaceInStoredProcedures: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void StoredProc_WhitespaceDifferenceInsideLiteral_StillReportedWhenIgnoreWhitespaceEnabled()
    {
        var procA = MakeProc("dbo", "WriteLog",
            "CREATE PROC WriteLog AS SELECT 'Hello   World'");
        var procB = MakeProc("dbo", "WriteLog",
            "CREATE PROC WriteLog AS SELECT 'Hello World'");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreCommentsInStoredProcedures: false,
            ignoreWhitespaceInStoredProcedures: true);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);
    }

    [Fact]
    public void StoredProc_RealCodeDifference_StillReportedWhenIgnoreWhitespaceEnabled()
    {
        var procA = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS SELECT 1");
        var procB = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS SELECT 2");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreCommentsInStoredProcedures: false,
            ignoreWhitespaceInStoredProcedures: true);

        Assert.Single(result.Changes);
    }

    [Fact]
    public void StoredProc_QuoteInsideLineComment_DoesNotConfuseLiteralParsing()
    {
        // -- it's a comment ... a stray quote inside a comment must not start
        // a fake string literal that swallows surrounding code.
        var procA = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS\n-- it's broken\nSELECT     1");
        var procB = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS\n-- it's broken\nSELECT 1");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreCommentsInStoredProcedures: false,
            ignoreWhitespaceInStoredProcedures: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void StoredProc_BothOptionsEnabled_StripsCommentsAndCollapsesWhitespace()
    {
        var procA = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS\n  -- v1\n  SELECT     1");
        var procB = MakeProc("dbo", "GetOrders",
            "CREATE PROC GetOrders AS /* v2 */ SELECT 1");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreCommentsInStoredProcedures: true,
            ignoreWhitespaceInStoredProcedures: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void StoredProc_BracketedAndUnbracketedNames_TreatedIdenticalWhenOptionEnabled()
    {
        var procA = MakeProc("dbo", "TestProc", "CREATE PROC [dbo].[TestProc] AS SELECT 1");
        var procB = MakeProc("dbo", "TestProc", "CREATE PROC dbo.TestProc AS SELECT 1");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreOptionalBrackets: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void StoredProc_BracketedAndUnbracketedNames_StillModifiedByDefault()
    {
        var procA = MakeProc("dbo", "TestProc", "CREATE PROC [dbo].[TestProc] AS SELECT 1");
        var procB = MakeProc("dbo", "TestProc", "CREATE PROC dbo.TestProc AS SELECT 1");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(a, b);

        Assert.Single(result.Changes);
    }

    [Fact]
    public void View_BracketStripping_AppliesToOtherObjectTypes()
    {
        var viewA = MakeView("dbo", "OrderSummary",
            "CREATE VIEW [dbo].[OrderSummary] AS SELECT 1 FROM [dbo].[Orders]");
        var viewB = MakeView("dbo", "OrderSummary",
            "CREATE VIEW dbo.OrderSummary AS SELECT 1 FROM dbo.Orders");
        var a = WithViews(EmptySchema("DbA"), viewA);
        var b = WithViews(EmptySchema("DbB"), viewB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreOptionalBrackets: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void BracketStripping_ReservedKeywordKeepsBrackets()
    {
        // [Order] is a reserved keyword — bracket-stripping must not produce
        // bare 'Order', which would parse as the keyword and break the query.
        var viewA = MakeView("dbo", "Reports",
            "CREATE VIEW [dbo].[Reports] AS SELECT * FROM [dbo].[Order]");
        var viewB = MakeView("dbo", "Reports",
            "CREATE VIEW dbo.Reports AS SELECT * FROM dbo.[Order]");
        var a = WithViews(EmptySchema("DbA"), viewA);
        var b = WithViews(EmptySchema("DbB"), viewB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreOptionalBrackets: true);

        // [dbo] dropped on both, [Order] kept on both → should collapse to same.
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void BracketStripping_NameWithSpaceKeepsBrackets()
    {
        // [Order Details] cannot be unbracketed — has a space.
        var viewA = MakeView("dbo", "Sales",
            "CREATE VIEW [dbo].[Sales] AS SELECT * FROM [dbo].[Order Details]");
        var viewB = MakeView("dbo", "Sales",
            "CREATE VIEW dbo.Sales AS SELECT * FROM dbo.[Order Details]");
        var a = WithViews(EmptySchema("DbA"), viewA);
        var b = WithViews(EmptySchema("DbB"), viewB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreOptionalBrackets: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Function_CommentOnlyDifference_SuppressedWhenIgnoreCommentsEnabled()
    {
        // Mirrors the user-reported case: a UDF whose only material difference
        // is a number inside a comment. With "ignore comments" on, the routine
        // normalizer (which now applies to functions, not just stored procs)
        // strips comments before the equality check.
        var funcA = MakeFunc("centurion", "GetSerial",
            "\nCREATE FUNCTION centurion.GetSerial(@id int) RETURNS bigint AS\n" +
            "-- SELECT centurion.GetSerial(37500067)\n" +
            "BEGIN RETURN 1 END\n");
        var funcB = MakeFunc("centurion", "GetSerial",
            "CREATE FUNCTION centurion.GetSerial(@id int) RETURNS bigint AS\n" +
            "-- SELECT centurion.GetSerial(37010225)\n" +
            "BEGIN RETURN 1 END");
        var a = WithFuncs(EmptySchema("DbA"), funcA);
        var b = WithFuncs(EmptySchema("DbB"), funcB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreCommentsInStoredProcedures: true,
            ignoreWhitespaceInStoredProcedures: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Function_RealUserScenario_WithAllThreeOptionsOn_NotModified()
    {
        // Reconstructed from the screenshot the user shared. The two sides
        // differ only inside a comment (the constant 37010225 vs 37500067),
        // a few whitespace placements, line wraps, and a leading blank line.
        // With ignore-comments + ignore-whitespace + ignore-brackets all on,
        // no Change should be reported.
        const string headerA =
            "\n" +
            "-- =================================================\n" +
            "-- Author:        Roy Lawson\n" +
            "-- Create date: 6/6/2024\n" +
            "-- Description: Scalar function that returns the Centurion serial number by DeviceId\n" +
            "-- Revisions:\n" +
            "-- 5/20/2025:   JMM    - Remove dash in serial number to support aWatches\n" +
            "-- SELECT [centurion].[GetCenturionSerialNumberByDeviceId](37500067)\n" +
            "-- =================================================\n";

        const string headerB =
            "-- =================================================\n" +
            "-- Author:        Roy Lawson\n" +
            "-- Create date: 6/6/2024\n" +
            "-- Description: Scalar function that returns the Centurion serial number by DeviceId\n" +
            "-- Revisions:\n" +
            "-- 5/20/2025:   JMM    - Remove dash in serial number to support aWatches\n" +
            "-- SELECT [centurion].[GetCenturionSerialNumberByDeviceId](37010225)\n" +
            "-- =================================================\n";

        const string body =
            "CREATE   FUNCTION [centurion].[GetCenturionSerialNumberByDeviceId](@DeviceId int)\n" +
            "RETURNS bigint\n" +
            "AS\n" +
            "BEGIN\n" +
            "DECLARE @Result bigint = 0;\n" +
            "\n" +
            "Select @Result = max(centurion.fn_SerialNumberToInt(AllDevices.SerialNumber))\n" +
            "FROM [dbo].[View_AllDevices]   as AllDevices with(nolock)\n" +
            "where DeviceId = @DeviceId;\n" +
            "\n" +
            "if @Result is null or @Result = 0\n" +
            "begin\n" +
            "  select @Result = max(CenturionSerialNumber) from [centurion].[CenturionDevices] with(nolock) where DeviceId = @DeviceId\n" +
            "end\n" +
            "\n" +
            "RETURN (coalesce(@Result,0))\n" +
            "END";

        var funcA = MakeFunc("centurion", "GetCenturionSerialNumberByDeviceId", headerA + body);
        var funcB = MakeFunc("centurion", "GetCenturionSerialNumberByDeviceId", headerB + body + "\n");
        var a = WithFuncs(EmptySchema("DbA"), funcA);
        var b = WithFuncs(EmptySchema("DbB"), funcB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreCommentsInStoredProcedures: true,
            ignoreWhitespaceInStoredProcedures: true,
            ignoreOptionalBrackets: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Function_CommentOnlyDifference_StillModifiedByDefault()
    {
        var funcA = MakeFunc("dbo", "Calc",
            "CREATE FUNCTION dbo.Calc() RETURNS int AS BEGIN -- v1\n RETURN 1 END");
        var funcB = MakeFunc("dbo", "Calc",
            "CREATE FUNCTION dbo.Calc() RETURNS int AS BEGIN -- v2\n RETURN 1 END");
        var a = WithFuncs(EmptySchema("DbA"), funcA);
        var b = WithFuncs(EmptySchema("DbB"), funcB);

        var result = SchemaComparator.Compare(a, b);

        Assert.Single(result.Changes);
    }

    [Fact]
    public void StoredProc_ProcVsProcedureKeyword_ComparesEqual()
    {
        // T-SQL treats PROC and PROCEDURE as the same keyword. SMO scripts
        // databases as "CREATE   PROC" (sometimes with extra whitespace)
        // while folder files written by SQLParity / typical SSMS scripts
        // use "CREATE PROCEDURE" — both must canonicalize to the same form.
        var procA = MakeProc("dbo", "Foo",
            "CREATE   PROC [dbo].[Foo] AS SELECT 1");
        var procB = MakeProc("dbo", "Foo",
            "CREATE OR ALTER PROCEDURE [dbo].[Foo] AS SELECT 1");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(a, b);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void StoredProc_CreateOrAlterVsCreate_ComparesEqual()
    {
        // After a folder sync, the file holds CREATE OR ALTER PROCEDURE while
        // the live DB scripts back as plain CREATE PROCEDURE. Both forms must
        // canonicalize to the same string so a freshly-synced proc doesn't
        // show as Modified on the next compare.
        var procA = MakeProc("dbo", "Foo",
            "CREATE PROCEDURE [dbo].[Foo] AS SELECT 1");
        var procB = MakeProc("dbo", "Foo",
            "CREATE OR ALTER PROCEDURE [dbo].[Foo] AS SELECT 1");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(a, b);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Function_CreateOrAlterVsCreate_ComparesEqual()
    {
        var fA = MakeFunc("dbo", "F",
            "CREATE FUNCTION [dbo].[F]() RETURNS INT AS BEGIN RETURN 1 END");
        var fB = MakeFunc("dbo", "F",
            "CREATE OR ALTER FUNCTION [dbo].[F]() RETURNS INT AS BEGIN RETURN 1 END");
        var a = WithFuncs(EmptySchema("DbA"), fA);
        var b = WithFuncs(EmptySchema("DbB"), fB);

        var result = SchemaComparator.Compare(a, b);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Table_FolderMode_IgnoresSetAndAlterBatches()
    {
        // Side A: full SMO output (SET / CREATE TABLE / ALTER TABLE constraint).
        // Side B: just the CREATE TABLE batch (what the parser captures from
        // a written file). With sideBIsFolder=true, both must canonicalize to
        // the CREATE batch alone.
        const string smo =
            "SET ANSI_NULLS ON\nGO\n" +
            "SET QUOTED_IDENTIFIER ON\nGO\n" +
            "CREATE TABLE [dbo].[Orders] (Id INT NOT NULL)\nGO\n" +
            "ALTER TABLE [dbo].[Orders] ADD CONSTRAINT DF_Orders_X DEFAULT ((0)) FOR [Id]\nGO\n";
        const string folder = "CREATE TABLE [dbo].[Orders] (Id INT NOT NULL)";

        var tA = MakeTable("dbo", "Orders", ddl: smo);
        var tB = MakeTable("dbo", "Orders", ddl: folder);
        var a = WithTables(EmptySchema("DbA"), tA);
        var b = WithTables(EmptySchema("DbB"), tB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            sideBIsFolder: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Table_FolderMode_StripsIfNotExistsWrapperAroundCreateOnSideB()
    {
        // What the parser actually captures from a SQLParity-generated file:
        // the entire "IF OBJECT_ID(…) IS NULL BEGIN <CREATE> END" batch. The
        // comparator must strip the wrapper before the equality check or every
        // freshly-synced table re-appears as Modified on the next compare.
        const string smo =
            "SET ANSI_NULLS ON\nGO\n" +
            "CREATE TABLE [dbo].[Orders] (Id INT NOT NULL)\nGO\n";
        const string folderWrapped =
            "IF OBJECT_ID(N'[dbo].[Orders]', N'U') IS NULL\n" +
            "BEGIN\n" +
            "    CREATE TABLE [dbo].[Orders] (Id INT NOT NULL)\n" +
            "END";

        var tA = MakeTable("dbo", "Orders", ddl: smo);
        var tB = MakeTable("dbo", "Orders", ddl: folderWrapped);
        var a = WithTables(EmptySchema("DbA"), tA);
        var b = WithTables(EmptySchema("DbB"), tB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            sideBIsFolder: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Table_FolderMode_StillDetectsRealCreateBatchDifference()
    {
        // Confirm the folder-mode normalizer hasn't disabled detection
        // entirely — a column-name change inside the CREATE TABLE batch
        // still surfaces as Modified.
        const string smo =
            "SET ANSI_NULLS ON\nGO\n" +
            "CREATE TABLE [dbo].[Orders] (Id INT NOT NULL)\nGO\n";
        const string folder = "CREATE TABLE [dbo].[Orders] (RenamedId INT NOT NULL)";

        var tA = MakeTable("dbo", "Orders", ddl: smo);
        var tB = MakeTable("dbo", "Orders", ddl: folder);
        var a = WithTables(EmptySchema("DbA"), tA);
        var b = WithTables(EmptySchema("DbB"), tB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            sideBIsFolder: true);

        Assert.Single(result.Changes);
    }

    [Fact]
    public void LimitToFolderObjects_DropsAOnlyChanges()
    {
        // Side A has a proc that doesn't exist in Side B (the "folder").
        // With the limit on, that A-only proc must not appear in changes.
        var procOnlyA = MakeProc("dbo", "OnlyInDb", "CREATE PROC OnlyInDb AS SELECT 1");
        var a = WithProcs(EmptySchema("DbA"), procOnlyA);
        var b = EmptySchema("Folder");

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            limitToFolderObjects: true);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void LimitToFolderObjects_KeepsModifiedChanges()
    {
        var procA = MakeProc("dbo", "Foo", "CREATE PROC Foo AS SELECT 1");
        var procB = MakeProc("dbo", "Foo", "CREATE PROC Foo AS SELECT 2");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("Folder"), procB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            limitToFolderObjects: true);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Modified, change.Status);
    }

    [Fact]
    public void LimitToFolderObjects_KeepsBOnlyChanges()
    {
        // The folder has an object that no longer exists in the live DB.
        // The user still wants to see that — they may want to delete the file
        // or restore the DB object.
        var procOnlyB = MakeProc("dbo", "OnlyInFile", "CREATE PROC OnlyInFile AS SELECT 1");
        var a = EmptySchema("DbA");
        var b = WithProcs(EmptySchema("Folder"), procOnlyB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            limitToFolderObjects: true);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.Dropped, change.Status);
    }

    [Fact]
    public void LimitToFolderObjects_OffPreservesAllChanges()
    {
        var procOnlyA = MakeProc("dbo", "OnlyInDb", "CREATE PROC OnlyInDb AS SELECT 1");
        var a = WithProcs(EmptySchema("DbA"), procOnlyA);
        var b = EmptySchema("Folder");

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            limitToFolderObjects: false);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeStatus.New, change.Status);
    }

    [Fact]
    public void BracketStripping_DoesNotTouchBracketsInsideStringLiteral()
    {
        // The literal '[dbo].[X]' is data, not an identifier — must stay intact.
        // Side A and B differ in the literal so they must still be Modified.
        var procA = MakeProc("dbo", "WriteLog",
            "CREATE PROC [dbo].[WriteLog] AS SELECT '[dbo].[Foo]'");
        var procB = MakeProc("dbo", "WriteLog",
            "CREATE PROC dbo.WriteLog AS SELECT '[dbo].[Bar]'");
        var a = WithProcs(EmptySchema("DbA"), procA);
        var b = WithProcs(EmptySchema("DbB"), procB);

        var result = SchemaComparator.Compare(
            a, b, SchemaReadOptions.All,
            ignoreOptionalBrackets: true);

        Assert.Single(result.Changes);
    }
}
