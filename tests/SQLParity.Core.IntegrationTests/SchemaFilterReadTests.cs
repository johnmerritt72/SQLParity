using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class SchemaFilterReadFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE SCHEMA [sales] AUTHORIZATION [dbo]
GO

CREATE TABLE [dbo].[DboTable] ([Id] INT NOT NULL PRIMARY KEY)
GO
CREATE TABLE [sales].[SalesTable] ([Id] INT NOT NULL PRIMARY KEY)
GO

CREATE VIEW [dbo].[DboView] AS SELECT 1 AS X
GO
CREATE VIEW [sales].[SalesView] AS SELECT 1 AS X
GO

CREATE PROCEDURE [dbo].[DboProc] AS BEGIN SELECT 1; END
GO
CREATE PROCEDURE [sales].[SalesProc] AS BEGIN SELECT 1; END
GO

CREATE FUNCTION [sales].[SalesFn]() RETURNS INT AS BEGIN RETURN 1; END
GO

CREATE SEQUENCE [dbo].[DboSeq] AS INT START WITH 1
GO
CREATE SEQUENCE [sales].[SalesSeq] AS INT START WITH 1
GO

CREATE USER [SchemaFilterPermUser] WITHOUT LOGIN
GO
GRANT SELECT ON [dbo].[DboTable] TO [SchemaFilterPermUser]
GO
GRANT SELECT ON [sales].[SalesTable] TO [SchemaFilterPermUser]
GO
GRANT SELECT ON SCHEMA::[dbo] TO [SchemaFilterPermUser]
GO
GRANT SELECT ON SCHEMA::[sales] TO [SchemaFilterPermUser]
GO
";
}

public class SchemaFilterReadTests : IClassFixture<SchemaFilterReadFixture>
{
    private readonly SchemaFilterReadFixture _fx;

    public SchemaFilterReadTests(SchemaFilterReadFixture fx) => _fx = fx;

    private DatabaseSchema Read(string schemaFilter) =>
        new SchemaReader(_fx.ConnectionString, _fx.DatabaseName)
            .ReadSchema(null, new SchemaReadOptions { SchemaFilter = schemaFilter });

    [Fact]
    public void FilteredRead_ReturnsOnlySelectedSchemaObjects()
    {
        var schema = Read("sales");

        Assert.Equal(new[] { "SalesTable" }, schema.Tables.Select(t => t.Name));
        Assert.Equal(new[] { "SalesView" }, schema.Views.Select(v => v.Name));
        Assert.Equal(new[] { "SalesProc" }, schema.StoredProcedures.Select(p => p.Name));
        Assert.Equal(new[] { "SalesFn" }, schema.Functions.Select(f => f.Name));
        Assert.Equal(new[] { "SalesSeq" }, schema.Sequences.Select(s => s.Name));
        Assert.All(schema.Tables, t => Assert.Equal("sales", t.Schema));
    }

    [Fact]
    public void FilteredRead_SchemasListContainsOnlySelectedSchema()
    {
        var schema = Read("sales");
        Assert.Equal(new[] { "sales" }, schema.Schemas.Select(s => s.Name));
    }

    [Fact]
    public void FilteredRead_PermissionsScopedToSelectedSchema()
    {
        var schema = Read("sales");
        var userPerms = schema.Permissions
            .Where(p => p.GranteeName == "SchemaFilterPermUser").ToList();

        Assert.All(userPerms, p => Assert.Equal("sales", p.TargetSchema));
        Assert.Contains(userPerms, p => p.Class == PermissionClass.Object && p.TargetName == "SalesTable");
        Assert.Contains(userPerms, p => p.Class == PermissionClass.Schema && p.TargetName == "sales");
        Assert.DoesNotContain(userPerms, p => p.TargetName == "DboTable");
    }

    [Fact]
    public void FilteredRead_IsCaseInsensitive()
    {
        var schema = Read("SALES");
        Assert.Equal(new[] { "SalesTable" }, schema.Tables.Select(t => t.Name));
    }

    [Fact]
    public void UnfilteredRead_ReturnsBothSchemas()
    {
        var schema = Read(null);

        Assert.Contains(schema.Tables, t => t.Name == "DboTable");
        Assert.Contains(schema.Tables, t => t.Name == "SalesTable");
        Assert.Contains(schema.Schemas, s => s.Name == "sales");
    }
}
