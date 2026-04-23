using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class MiscTestFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE SCHEMA [inventory]
GO

CREATE SEQUENCE [dbo].[OrderNumberSeq]
    AS BIGINT
    START WITH 1000
    INCREMENT BY 1
GO

CREATE TABLE [dbo].[Placeholder] (
    [Id] INT NOT NULL PRIMARY KEY
)
GO

CREATE SYNONYM [dbo].[PlaceholderAlias] FOR [dbo].[Placeholder]
GO

CREATE TYPE [dbo].[PhoneNumber] FROM NVARCHAR(20) NOT NULL
GO

CREATE TYPE [dbo].[OrderLineItem] AS TABLE (
    [LineNumber] INT NOT NULL,
    [ProductName] NVARCHAR(200) NOT NULL,
    [Quantity] INT NOT NULL,
    [UnitPrice] DECIMAL(18,2) NOT NULL
)
GO
";
}

public class SchemaReaderMiscTests : IClassFixture<MiscTestFixture>
{
    private readonly DatabaseSchema _schema;

    public SchemaReaderMiscTests(MiscTestFixture fixture)
    {
        var reader = new SchemaReader(fixture.ConnectionString, fixture.DatabaseName);
        _schema = reader.ReadSchema();
    }

    [Fact]
    public void ReadsUserSchema()
    {
        var schema = _schema.Schemas.SingleOrDefault(s => s.Name == "inventory");
        Assert.NotNull(schema);
        Assert.False(string.IsNullOrWhiteSpace(schema.Ddl));
    }

    [Fact]
    public void ExcludesSystemSchemas()
    {
        Assert.DoesNotContain(_schema.Schemas, s => s.Name == "sys");
        Assert.DoesNotContain(_schema.Schemas, s => s.Name == "INFORMATION_SCHEMA");
    }

    [Fact]
    public void ReadsSequence()
    {
        var seq = _schema.Sequences.SingleOrDefault(s => s.Name == "OrderNumberSeq");
        Assert.NotNull(seq);
        Assert.Equal("dbo", seq.Schema);
        Assert.False(string.IsNullOrWhiteSpace(seq.Ddl));
    }

    [Fact]
    public void ReadsSynonym()
    {
        var syn = _schema.Synonyms.SingleOrDefault(s => s.Name == "PlaceholderAlias");
        Assert.NotNull(syn);
        Assert.Equal("dbo", syn.Schema);
        Assert.Contains("Placeholder", syn.BaseObject);
    }

    [Fact]
    public void ReadsUserDefinedDataType()
    {
        var udt = _schema.UserDefinedDataTypes.SingleOrDefault(u => u.Name == "PhoneNumber");
        Assert.NotNull(udt);
        Assert.Equal("dbo", udt.Schema);
        Assert.Contains("nvarchar", udt.BaseType, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadsUserDefinedTableType()
    {
        var udtt = _schema.UserDefinedTableTypes.SingleOrDefault(u => u.Name == "OrderLineItem");
        Assert.NotNull(udtt);
        Assert.Equal("dbo", udtt.Schema);
        Assert.Equal(4, udtt.Columns.Count);
        Assert.Contains(udtt.Columns, c => c.Name == "LineNumber");
        Assert.Contains(udtt.Columns, c => c.Name == "ProductName");
        Assert.Contains(udtt.Columns, c => c.Name == "Quantity");
        Assert.Contains(udtt.Columns, c => c.Name == "UnitPrice");
    }

    [Fact]
    public void DatabaseSchemaHasServerAndDbName()
    {
        Assert.False(string.IsNullOrWhiteSpace(_schema.ServerName));
        Assert.False(string.IsNullOrWhiteSpace(_schema.DatabaseName));
        Assert.True(_schema.ReadAtUtc > System.DateTime.MinValue);
    }
}
