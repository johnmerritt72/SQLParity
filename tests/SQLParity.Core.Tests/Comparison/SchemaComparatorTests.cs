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
}
