using System;
using System.Collections.Generic;
using SQLParity.Core.Model;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.Tests.Sync;

public class AlterTableGeneratorTests
{
    private static ColumnModel MakeCol(string name, string type = "Int", int maxLen = 0,
        bool nullable = false, DefaultConstraintModel dc = null) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", "Orders", name),
        Name = name, DataType = type, MaxLength = maxLen, Precision = 0, Scale = 0,
        IsNullable = nullable, IsIdentity = false, IdentitySeed = 0, IdentityIncrement = 0,
        IsComputed = false, ComputedText = null, IsPersisted = false, Collation = null,
        DefaultConstraint = dc, OrdinalPosition = 0,
    };

    private static ColumnChange MakeColumnChange(ChangeStatus status, ColumnModel sideA, ColumnModel sideB = null) => new()
    {
        Id = SchemaQualifiedName.Child("dbo", "Orders", sideA?.Name ?? sideB?.Name ?? "Unknown"),
        ColumnName = sideA?.Name ?? sideB?.Name ?? "Unknown",
        Status = status,
        SideA = sideA,
        SideB = sideB,
    };

    [Fact]
    public void NewNullableColumn_GeneratesAddColumn()
    {
        var col = MakeCol("Notes", "NVarChar", maxLen: 200, nullable: true);
        var change = MakeColumnChange(ChangeStatus.New, sideA: col, sideB: null);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ADD", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Notes", sql);
        Assert.Contains("NULL", sql);
    }

    [Fact]
    public void NewNotNullColumnWithDefault_GeneratesAddWithDefault()
    {
        var dc = new DefaultConstraintModel { Name = "DF_Orders_IsActive", Definition = "(1)" };
        var col = MakeCol("IsActive", "Bit", nullable: false, dc: dc);
        var change = MakeColumnChange(ChangeStatus.New, sideA: col, sideB: null);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ADD", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT NULL", sql);
        Assert.Contains("DEFAULT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DroppedColumn_GeneratesDropColumn()
    {
        var col = MakeCol("OldCol", "Int");
        var change = MakeColumnChange(ChangeStatus.Dropped, sideA: null, sideB: col);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("DROP COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OldCol", sql);
    }

    [Fact]
    public void ModifiedColumn_TypeChange_GeneratesAlterColumn()
    {
        var sideB = MakeCol("Amount", "Int");
        var sideA = MakeCol("Amount", "BigInt");
        var change = MakeColumnChange(ChangeStatus.Modified, sideA: sideA, sideB: sideB);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ALTER COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Amount", sql);
    }

    [Fact]
    public void ModifiedColumn_Widened_GeneratesAlterColumn()
    {
        var sideB = MakeCol("Name", "NVarChar", maxLen: 100);
        var sideA = MakeCol("Name", "NVarChar", maxLen: 200);
        var change = MakeColumnChange(ChangeStatus.Modified, sideA: sideA, sideB: sideB);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ALTER COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("200", sql);
    }

    [Fact]
    public void ModifiedColumn_NullabilityChange_GeneratesAlterColumn()
    {
        var sideB = MakeCol("Status", "Int", nullable: false);
        var sideA = MakeCol("Status", "Int", nullable: true);
        var change = MakeColumnChange(ChangeStatus.Modified, sideA: sideA, sideB: sideB);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ALTER COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NULL", sql);
    }

    [Fact]
    public void ModifiedTable_GeneratesAllColumnAlters()
    {
        var changes = new List<ColumnChange>
        {
            MakeColumnChange(ChangeStatus.New, sideA: MakeCol("NewCol", "Int"), sideB: null),
            MakeColumnChange(ChangeStatus.Dropped, sideA: null, sideB: MakeCol("OldCol", "Int")),
        };

        var sql = AlterTableGenerator.GenerateForModifiedTable("dbo", "Orders", changes);

        Assert.Contains("ADD", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DROP COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GO", sql);
    }

    [Fact]
    public void DefaultConstraintAdded_GeneratesAddConstraint()
    {
        var dc = new DefaultConstraintModel { Name = "DF_Orders_CreatedAt", Definition = "(GETUTCDATE())" };
        var sideB = MakeCol("CreatedAt", "DateTime2", dc: null);
        var sideA = MakeCol("CreatedAt", "DateTime2", dc: dc);
        var change = MakeColumnChange(ChangeStatus.Modified, sideA: sideA, sideB: sideB);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("ADD CONSTRAINT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEFAULT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DF_Orders_CreatedAt", sql);
    }

    [Fact]
    public void DefaultConstraintRemoved_GeneratesDropConstraint()
    {
        var dc = new DefaultConstraintModel { Name = "DF_Orders_Status", Definition = "(0)" };
        var sideB = MakeCol("Status", "Int", dc: dc);
        var sideA = MakeCol("Status", "Int", dc: null);
        var change = MakeColumnChange(ChangeStatus.Modified, sideA: sideA, sideB: sideB);

        var sql = AlterTableGenerator.GenerateColumnAlter("dbo", "Orders", change);

        Assert.Contains("DROP CONSTRAINT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DF_Orders_Status", sql);
    }
}
