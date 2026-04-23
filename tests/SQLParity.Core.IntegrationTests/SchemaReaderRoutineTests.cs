using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public sealed class RoutineTestFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE TABLE [dbo].[Products] (
    [ProductId] INT NOT NULL IDENTITY(1,1),
    [Name] NVARCHAR(200) NOT NULL,
    [Price] DECIMAL(18,2) NOT NULL,
    CONSTRAINT [PK_Products] PRIMARY KEY CLUSTERED ([ProductId])
)
GO

CREATE VIEW [dbo].[ExpensiveProducts]
WITH SCHEMABINDING
AS
    SELECT [ProductId], [Name], [Price]
    FROM [dbo].[Products]
    WHERE [Price] > 100
GO

CREATE PROCEDURE [dbo].[GetProductById]
    @ProductId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [ProductId], [Name], [Price]
    FROM [dbo].[Products]
    WHERE [ProductId] = @ProductId;
END
GO

CREATE FUNCTION [dbo].[GetProductCount]()
RETURNS INT
AS
BEGIN
    DECLARE @count INT;
    SELECT @count = COUNT(*) FROM [dbo].[Products];
    RETURN @count;
END
GO

CREATE FUNCTION [dbo].[GetProductsAbovePrice](@MinPrice DECIMAL(18,2))
RETURNS TABLE
AS
RETURN (
    SELECT [ProductId], [Name], [Price]
    FROM [dbo].[Products]
    WHERE [Price] > @MinPrice
)
GO
";
}

public class SchemaReaderRoutineTests : IClassFixture<RoutineTestFixture>
{
    private readonly DatabaseSchema _schema;

    public SchemaReaderRoutineTests(RoutineTestFixture fixture)
    {
        var reader = new SchemaReader(fixture.ConnectionString, fixture.DatabaseName);
        _schema = reader.ReadSchema();
    }

    [Fact]
    public void ReadsView()
    {
        var view = _schema.Views.SingleOrDefault(v => v.Name == "ExpensiveProducts");
        Assert.NotNull(view);
        Assert.Equal("dbo", view.Schema);
        Assert.True(view.IsSchemaBound);
        Assert.False(string.IsNullOrWhiteSpace(view.Ddl));
        Assert.Contains("CREATE", view.Ddl);
    }

    [Fact]
    public void ReadsStoredProcedure()
    {
        var sp = _schema.StoredProcedures.SingleOrDefault(s => s.Name == "GetProductById");
        Assert.NotNull(sp);
        Assert.Equal("dbo", sp.Schema);
        Assert.False(string.IsNullOrWhiteSpace(sp.Ddl));
        Assert.Contains("CREATE", sp.Ddl);
    }

    [Fact]
    public void ReadsScalarFunction()
    {
        var fn = _schema.Functions.SingleOrDefault(f => f.Name == "GetProductCount");
        Assert.NotNull(fn);
        Assert.Equal(FunctionKind.Scalar, fn.Kind);
        Assert.False(string.IsNullOrWhiteSpace(fn.Ddl));
    }

    [Fact]
    public void ReadsInlineTableValuedFunction()
    {
        var fn = _schema.Functions.SingleOrDefault(f => f.Name == "GetProductsAbovePrice");
        Assert.NotNull(fn);
        Assert.Equal(FunctionKind.InlineTableValued, fn.Kind);
        Assert.False(string.IsNullOrWhiteSpace(fn.Ddl));
    }
}
