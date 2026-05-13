using System;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Model;

public class DatabaseSchemaTests
{
    private static DatabaseSchema EmptySchema() => new()
    {
        ServerName = "localhost",
        DatabaseName = "TestDb",
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

    private static DatabaseSchema WithSchemas(DatabaseSchema baseSchema, params SchemaModel[] schemas) => new()
    {
        ServerName = baseSchema.ServerName,
        DatabaseName = baseSchema.DatabaseName,
        ReadAtUtc = baseSchema.ReadAtUtc,
        Schemas = schemas,
        Tables = baseSchema.Tables,
        Views = baseSchema.Views,
        StoredProcedures = baseSchema.StoredProcedures,
        Functions = baseSchema.Functions,
        Sequences = baseSchema.Sequences,
        Synonyms = baseSchema.Synonyms,
        UserDefinedDataTypes = baseSchema.UserDefinedDataTypes,
        UserDefinedTableTypes = baseSchema.UserDefinedTableTypes,
    };

    private static DatabaseSchema WithTables(DatabaseSchema baseSchema, params TableModel[] tables) => new()
    {
        ServerName = baseSchema.ServerName,
        DatabaseName = baseSchema.DatabaseName,
        ReadAtUtc = baseSchema.ReadAtUtc,
        Schemas = baseSchema.Schemas,
        Tables = tables,
        Views = baseSchema.Views,
        StoredProcedures = baseSchema.StoredProcedures,
        Functions = baseSchema.Functions,
        Sequences = baseSchema.Sequences,
        Synonyms = baseSchema.Synonyms,
        UserDefinedDataTypes = baseSchema.UserDefinedDataTypes,
        UserDefinedTableTypes = baseSchema.UserDefinedTableTypes,
    };

    private static DatabaseSchema WithProcs(DatabaseSchema baseSchema, params StoredProcedureModel[] procs) => new()
    {
        ServerName = baseSchema.ServerName,
        DatabaseName = baseSchema.DatabaseName,
        ReadAtUtc = baseSchema.ReadAtUtc,
        Schemas = baseSchema.Schemas,
        Tables = baseSchema.Tables,
        Views = baseSchema.Views,
        StoredProcedures = procs,
        Functions = baseSchema.Functions,
        Sequences = baseSchema.Sequences,
        Synonyms = baseSchema.Synonyms,
        UserDefinedDataTypes = baseSchema.UserDefinedDataTypes,
        UserDefinedTableTypes = baseSchema.UserDefinedTableTypes,
    };

    private static TableModel MakeTable(string schema, string name) => new()
    {
        Id = SchemaQualifiedName.TopLevel(schema, name),
        Schema = schema,
        Name = name,
        Ddl = "CREATE TABLE ...",
        Columns = Array.Empty<ColumnModel>(),
        Indexes = Array.Empty<IndexModel>(),
        ForeignKeys = Array.Empty<ForeignKeyModel>(),
        CheckConstraints = Array.Empty<CheckConstraintModel>(),
        Triggers = Array.Empty<TriggerModel>(),
    };

    private static StoredProcedureModel MakeProc(string schema, string name) => new()
    {
        Id = SchemaQualifiedName.TopLevel(schema, name),
        Schema = schema,
        Name = name,
        Ddl = "CREATE PROC ...",
    };

    [Fact]
    public void HasObjects_AllListsEmpty_ReturnsFalse()
    {
        var schema = EmptySchema();
        Assert.False(schema.HasObjects);
    }

    [Fact]
    public void HasObjects_OnlyDboSchema_NoOtherObjects_ReturnsFalse()
    {
        // A live empty DB still has the dbo schema; HasObjects must exclude it.
        var schema = WithSchemas(
            EmptySchema(),
            new SchemaModel { Name = "dbo", Owner = "dbo", Ddl = "CREATE SCHEMA dbo" });
        Assert.False(schema.HasObjects);
    }

    [Fact]
    public void HasObjects_OneTable_ReturnsTrue()
    {
        var schema = WithTables(EmptySchema(), MakeTable("dbo", "T"));
        Assert.True(schema.HasObjects);
    }

    [Fact]
    public void HasObjects_OneStoredProcedureOnly_ReturnsTrue()
    {
        var schema = WithProcs(EmptySchema(), MakeProc("dbo", "P"));
        Assert.True(schema.HasObjects);
    }
}
