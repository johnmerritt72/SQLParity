using System.Linq;
using SQLParity.Core;
using SQLParity.Core.Sync;
using Xunit;
using Xunit.Abstractions;

namespace SQLParity.Core.IntegrationTests;

public sealed class DestinationCountQueryFixture : ThrowawayDatabaseFixture
{
    protected override string SetupSql() => @"
CREATE SCHEMA [sales] AUTHORIZATION [dbo]
GO

CREATE TABLE [dbo].[RegularTable] ([Id] INT NOT NULL PRIMARY KEY)
GO
CREATE TABLE [sales].[SalesTable] ([Id] INT NOT NULL PRIMARY KEY)
GO

CREATE VIEW [dbo].[RegularView] AS SELECT 1 AS X
GO
CREATE VIEW [sales].[SalesView] AS SELECT 1 AS X
GO

CREATE PROCEDURE [dbo].[GetFoo] AS BEGIN SELECT 1; END
GO
CREATE PROCEDURE [sales].[SalesProc] AS BEGIN SELECT 1; END
GO

CREATE PROCEDURE [dbo].[sp_GetFoo] AS BEGIN SELECT 1; END
GO

-- The 'microsoft_database_tools_support' extended property is what SSMS
-- attaches when you install database-diagram support (sysdiagrams table,
-- sp_helpdiagrams / sp_creatediagram / etc.). SMO's IsSystemObject filter
-- treats objects carrying this property as system and skips them. The raw
-- 'sys.{tables,views,procedures}.is_ms_shipped' flag does not — so any
-- count query the pre-Apply verify uses must apply the same filter or
-- it'll report a phantom drift every time.
CREATE PROCEDURE [dbo].[DiagramLikeProc] AS BEGIN SELECT 1; END
GO
EXEC sys.sp_addextendedproperty
    @name = N'microsoft_database_tools_support',
    @value = 1,
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'PROCEDURE', @level1name = N'DiagramLikeProc';
GO

CREATE TABLE [dbo].[DiagramLikeTable] ([Id] INT NOT NULL PRIMARY KEY)
GO
EXEC sys.sp_addextendedproperty
    @name = N'microsoft_database_tools_support',
    @value = 1,
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'DiagramLikeTable';
GO

CREATE VIEW [dbo].[DiagramLikeView] AS SELECT 1 AS X
GO
EXEC sys.sp_addextendedproperty
    @name = N'microsoft_database_tools_support',
    @value = 1,
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'VIEW', @level1name = N'DiagramLikeView';
GO
";
}

public class DestinationCountQueryTests : IClassFixture<DestinationCountQueryFixture>
{
    private readonly DestinationCountQueryFixture _fx;
    private readonly ITestOutputHelper _out;

    public DestinationCountQueryTests(DestinationCountQueryFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    [Fact]
    public void DestinationCountQuery_MatchesSchemaReader_EvenWithSsmsDiagramFlaggedObjects()
    {
        // The pre-Apply verify path compares snapshot count (SchemaReader)
        // against now count (DestinationCountQuery). If those use different
        // filters, the diff trips a false "destination has changed" prompt on
        // every Generate-Script / Apply-Live click — even when nothing moved.
        var schema = new SchemaReader(_fx.ConnectionString, _fx.DatabaseName).ReadSchema();
        var counts = DestinationCountQuery.Read(_fx.ConnectionString);

        _out.WriteLine($"SchemaReader          — tables={schema.Tables.Count}, views={schema.Views.Count}, procs={schema.StoredProcedures.Count}");
        _out.WriteLine($"DestinationCountQuery — tables={counts.tables},      views={counts.views},      procs={counts.procs}");
        _out.WriteLine($"SchemaReader names — procs: {string.Join(", ", schema.StoredProcedures.Select(p => $"[{p.Schema}].[{p.Name}]"))}");

        Assert.Equal(schema.Tables.Count, counts.tables);
        Assert.Equal(schema.Views.Count, counts.views);
        Assert.Equal(schema.StoredProcedures.Count, counts.procs);
    }

    [Fact]
    public void DestinationCountQuery_UnscopedCall_CountsAllSchemas()
    {
        // Baseline: no schemaFilter argument still counts objects across
        // every schema (dbo AND sales), unchanged by adding the optional
        // parameter.
        var counts = DestinationCountQuery.Read(_fx.ConnectionString);

        // Tables are not excluded by the tools-support filter (only views and
        // procs are, per the SMO IsSystemObject behavior documented on
        // DestinationCountQuery), so DiagramLikeTable is counted here.
        // dbo: RegularTable, DiagramLikeTable; sales: SalesTable
        Assert.Equal(3, counts.tables);
        // dbo: RegularView (DiagramLikeView excluded); sales: SalesView
        Assert.Equal(2, counts.views);
        // dbo: GetFoo, sp_GetFoo (DiagramLikeProc excluded); sales: SalesProc
        Assert.Equal(3, counts.procs);
    }

    [Fact]
    public void DestinationCountQuery_ScopedToSales_CountsOnlySalesSchemaObjects()
    {
        var counts = DestinationCountQuery.Read(_fx.ConnectionString, "sales");

        Assert.Equal(1, counts.tables); // SalesTable
        Assert.Equal(1, counts.views);  // SalesView
        Assert.Equal(1, counts.procs);  // SalesProc
    }

    [Fact]
    public void DestinationCountQuery_ScopedToDbo_CountsOnlyDboSchemaObjects()
    {
        var counts = DestinationCountQuery.Read(_fx.ConnectionString, "dbo");

        // RegularTable, DiagramLikeTable (tables aren't excluded by the
        // tools-support filter)
        Assert.Equal(2, counts.tables);
        // RegularView (DiagramLikeView excluded)
        Assert.Equal(1, counts.views);
        // GetFoo, sp_GetFoo (DiagramLikeProc excluded)
        Assert.Equal(2, counts.procs);
    }

    [Fact]
    public void DestinationCountQuery_ScopedCounts_MatchSchemaReaderFilteredRead()
    {
        // The scoped count must agree with what a schema-filtered SchemaReader
        // read would have snapshotted — that's the invariant the pre-Apply
        // drift check under a schema filter depends on.
        var schema = new SchemaReader(_fx.ConnectionString, _fx.DatabaseName)
            .ReadSchema(null, new SchemaReadOptions { SchemaFilter = "sales" });
        var counts = DestinationCountQuery.Read(_fx.ConnectionString, "sales");

        Assert.Equal(schema.Tables.Count, counts.tables);
        Assert.Equal(schema.Views.Count, counts.views);
        Assert.Equal(schema.StoredProcedures.Count, counts.procs);
    }

    [Fact]
    public void DestinationCountQuery_ScopedToSchemaWithNoObjects_ReturnsZero()
    {
        var counts = DestinationCountQuery.Read(_fx.ConnectionString, "nonexistent_schema");

        Assert.Equal(0, counts.tables);
        Assert.Equal(0, counts.views);
        Assert.Equal(0, counts.procs);
    }
}
