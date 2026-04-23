using System;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class PreFlightQueryBuilderTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Change DroppedTableChange(string schema = "dbo", string name = "Orders") =>
        new Change
        {
            Id = SchemaQualifiedName.TopLevel(schema, name),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.Dropped,
            DdlSideA = null,
            DdlSideB = "CREATE TABLE ...",
            ColumnChanges = Array.Empty<ColumnChange>(),
            Risk = RiskTier.Destructive,
        };

    private static Change SafeChange() =>
        new Change
        {
            Id = SchemaQualifiedName.TopLevel("dbo", "Orders"),
            ObjectType = ObjectType.Table,
            Status = ChangeStatus.New,
            DdlSideA = "CREATE TABLE ...",
            DdlSideB = null,
            ColumnChanges = Array.Empty<ColumnChange>(),
            Risk = RiskTier.Safe,
        };

    private static Change DroppedForeignKeyChange(string schema = "dbo", string table = "Orders", string name = "FK_Orders_Customers") =>
        new Change
        {
            Id = SchemaQualifiedName.Child(schema, table, name),
            ObjectType = ObjectType.ForeignKey,
            Status = ChangeStatus.Dropped,
            DdlSideA = null,
            DdlSideB = "ALTER TABLE ...",
            ColumnChanges = Array.Empty<ColumnChange>(),
            Risk = RiskTier.Destructive,
        };

    private static ColumnModel MakeColumn(string table, string name, string dataType = "nvarchar", int maxLen = 100, bool nullable = true) =>
        new ColumnModel
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
            OrdinalPosition = 1,
        };

    private static ColumnChange DroppedColumnChange(string colName = "ColName") =>
        new ColumnChange
        {
            Id = SchemaQualifiedName.Child("dbo", "Orders", colName),
            ColumnName = colName,
            Status = ChangeStatus.Dropped,
            SideA = null,
            SideB = MakeColumn("Orders", colName),
            Risk = RiskTier.Destructive,
        };

    private static ColumnChange NarrowedColumnChange(string colName = "Description", int newLen = 50, int oldLen = 200) =>
        new ColumnChange
        {
            Id = SchemaQualifiedName.Child("dbo", "Orders", colName),
            ColumnName = colName,
            Status = ChangeStatus.Modified,
            SideA = MakeColumn("Orders", colName, "nvarchar", newLen),  // SideA = target (narrower)
            SideB = MakeColumn("Orders", colName, "nvarchar", oldLen),  // SideB = source (wider)
            Risk = RiskTier.Risky,
        };

    private static ColumnChange TypeChangedColumnChange(string colName = "Amount") =>
        new ColumnChange
        {
            Id = SchemaQualifiedName.Child("dbo", "Orders", colName),
            ColumnName = colName,
            Status = ChangeStatus.Modified,
            SideA = MakeColumn("Orders", colName, "bigint", 0),
            SideB = MakeColumn("Orders", colName, "int", 0),
            Risk = RiskTier.Risky,
        };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DroppedTable_ProducesRowCountQuery()
    {
        var change = DroppedTableChange("dbo", "Orders");

        var result = PreFlightQueryBuilder.Build(change);

        Assert.NotNull(result);
        Assert.Contains("SELECT COUNT(*)", result!.Value.Sql);
        Assert.Contains("[dbo].[Orders]", result!.Value.Sql);
    }

    [Fact]
    public void DroppedColumn_ProducesNonNullCountQuery()
    {
        var colChange = DroppedColumnChange("ColName");

        var result = PreFlightQueryBuilder.BuildForColumn("dbo", "Orders", colChange);

        Assert.NotNull(result);
        Assert.Contains("SELECT COUNT(*)", result!.Value.Sql);
        Assert.Contains("[ColName]", result!.Value.Sql);
        Assert.Contains("IS NOT NULL", result!.Value.Sql);
    }

    [Fact]
    public void NarrowedColumn_ProducesLengthCheckQuery()
    {
        var colChange = NarrowedColumnChange("Description", newLen: 50, oldLen: 200);

        var result = PreFlightQueryBuilder.BuildForColumn("dbo", "Orders", colChange);

        Assert.NotNull(result);
        Assert.Contains("LEN(", result!.Value.Sql);
        Assert.Contains("50", result!.Value.Sql);
    }

    [Fact]
    public void SafeChange_ReturnsNull()
    {
        var change = SafeChange();

        var result = PreFlightQueryBuilder.Build(change);

        Assert.Null(result);
    }

    [Fact]
    public void DroppedForeignKey_ProducesDescription()
    {
        var change = DroppedForeignKeyChange("dbo", "Orders", "FK_Orders_Customers");

        var result = PreFlightQueryBuilder.Build(change);

        Assert.NotNull(result);
        Assert.Contains("referential integrity", result!.Value.Description);
    }
}
