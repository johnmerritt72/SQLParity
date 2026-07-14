using System;
using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests;

public class DatabaseSchemaFilterTests
{
    private static DatabaseSchema TwoSchemaDb() => new()
    {
        ServerName = "localhost",
        DatabaseName = "TestDb",
        ReadAtUtc = DateTime.UtcNow,
        Schemas = new[]
        {
            new SchemaModel { Name = "dbo", Owner = "dbo", Ddl = "CREATE SCHEMA dbo" },
            new SchemaModel { Name = "sales", Owner = "dbo", Ddl = "CREATE SCHEMA sales" },
        },
        Tables = new[] { MakeTable("dbo", "DboTable"), MakeTable("sales", "SalesTable") },
        Views = new[]
        {
            new ViewModel { Id = SchemaQualifiedName.TopLevel("dbo", "DboView"), Schema = "dbo", Name = "DboView", IsSchemaBound = false, Ddl = "..." },
            new ViewModel { Id = SchemaQualifiedName.TopLevel("sales", "SalesView"), Schema = "sales", Name = "SalesView", IsSchemaBound = false, Ddl = "..." },
        },
        StoredProcedures = new[]
        {
            new StoredProcedureModel { Id = SchemaQualifiedName.TopLevel("dbo", "DboProc"), Schema = "dbo", Name = "DboProc", Ddl = "..." },
            new StoredProcedureModel { Id = SchemaQualifiedName.TopLevel("sales", "SalesProc"), Schema = "sales", Name = "SalesProc", Ddl = "..." },
        },
        Functions = new[]
        {
            new UserDefinedFunctionModel { Id = SchemaQualifiedName.TopLevel("sales", "SalesFn"), Schema = "sales", Name = "SalesFn", Kind = FunctionKind.Scalar, Ddl = "..." },
        },
        Sequences = new[]
        {
            new SequenceModel { Id = SchemaQualifiedName.TopLevel("dbo", "DboSeq"), Schema = "dbo", Name = "DboSeq", DataType = "Int", Ddl = "..." },
        },
        Synonyms = new[]
        {
            new SynonymModel { Id = SchemaQualifiedName.TopLevel("sales", "SalesSyn"), Schema = "sales", Name = "SalesSyn", BaseObject = "x", Ddl = "..." },
        },
        UserDefinedDataTypes = Array.Empty<UserDefinedDataTypeModel>(),
        UserDefinedTableTypes = Array.Empty<UserDefinedTableTypeModel>(),
        Permissions = new[]
        {
            new PermissionModel { Class = PermissionClass.Object, GranteeName = "u", PermissionName = "SELECT", State = PermissionState.Grant, TargetSchema = "dbo", TargetName = "DboTable" },
            new PermissionModel { Class = PermissionClass.Object, GranteeName = "u", PermissionName = "SELECT", State = PermissionState.Grant, TargetSchema = "sales", TargetName = "SalesTable" },
            new PermissionModel { Class = PermissionClass.Schema, GranteeName = "u", PermissionName = "SELECT", State = PermissionState.Grant, TargetSchema = "sales", TargetName = "sales" },
        },
        ExternalReferences = new System.Collections.Generic.Dictionary<(string, string), System.Collections.Generic.IReadOnlyList<string>>
        {
            [("dbo", "DboView")] = new[] { "OtherDb" },
            [("sales", "SalesView")] = new[] { "OtherDb" },
        },
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

    [Fact]
    public void FilterToSchema_KeepsOnlyMatchingObjects()
    {
        var filtered = DatabaseSchemaFilter.FilterToSchema(TwoSchemaDb(), "sales");

        Assert.Equal(new[] { "sales" }, filtered.Schemas.Select(s => s.Name));
        Assert.Equal(new[] { "SalesTable" }, filtered.Tables.Select(t => t.Name));
        Assert.Equal(new[] { "SalesView" }, filtered.Views.Select(v => v.Name));
        Assert.Equal(new[] { "SalesProc" }, filtered.StoredProcedures.Select(p => p.Name));
        Assert.Equal(new[] { "SalesFn" }, filtered.Functions.Select(f => f.Name));
        Assert.Empty(filtered.Sequences);
        Assert.Equal(new[] { "SalesSyn" }, filtered.Synonyms.Select(s => s.Name));
    }

    [Fact]
    public void FilterToSchema_FiltersPermissionsToObjectAndSchemaLevel()
    {
        var filtered = DatabaseSchemaFilter.FilterToSchema(TwoSchemaDb(), "sales");

        Assert.Equal(2, filtered.Permissions.Count);
        Assert.All(filtered.Permissions, p => Assert.Equal("sales", p.TargetSchema));
        Assert.Contains(filtered.Permissions, p => p.Class == PermissionClass.Object && p.TargetName == "SalesTable");
        Assert.Contains(filtered.Permissions, p => p.Class == PermissionClass.Schema && p.TargetName == "sales");
    }

    [Fact]
    public void FilterToSchema_FiltersExternalReferences()
    {
        var filtered = DatabaseSchemaFilter.FilterToSchema(TwoSchemaDb(), "sales");

        Assert.Single(filtered.ExternalReferences);
        Assert.True(filtered.ExternalReferences.ContainsKey(("sales", "SalesView")));
    }

    [Fact]
    public void FilterToSchema_IsCaseInsensitive()
    {
        var filtered = DatabaseSchemaFilter.FilterToSchema(TwoSchemaDb(), "SALES");
        Assert.Equal(new[] { "SalesTable" }, filtered.Tables.Select(t => t.Name));
    }

    [Fact]
    public void FilterToSchema_PreservesIdentityMetadata()
    {
        var source = TwoSchemaDb();
        var filtered = DatabaseSchemaFilter.FilterToSchema(source, "sales");

        Assert.Equal(source.ServerName, filtered.ServerName);
        Assert.Equal(source.DatabaseName, filtered.DatabaseName);
        Assert.Equal(source.ReadAtUtc, filtered.ReadAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void FilterToSchema_NullOrWhitespaceFilter_ReturnsSourceUnchanged(string? filter)
    {
        var source = TwoSchemaDb();
        Assert.Same(source, DatabaseSchemaFilter.FilterToSchema(source, filter));
    }

    [Fact]
    public void FilterPermissions_KeepsOnlyMatchingTargetSchema()
    {
        var perms = TwoSchemaDb().Permissions;
        var filtered = DatabaseSchemaFilter.FilterPermissions(perms, "dbo");
        Assert.Single(filtered);
        Assert.Equal("DboTable", filtered[0].TargetName);
    }
}
