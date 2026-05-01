using System;
using System.IO;
using System.Linq;
using SQLParity.Core.Model;
using SQLParity.Core.Parsing;
using Xunit;

namespace SQLParity.Core.Tests.Parsing;

public class FolderSchemaReaderTests
{
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; }
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "SQLParity_TEST_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string WriteFile(string name, string contents)
        {
            var full = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
            return full;
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    private static FolderSchemaReader Reader => new();

    [Fact]
    public void NonExistentFolder_Throws()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "SQLParity_DoesNotExist_" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => Reader.ReadFolder(bogus, "srv", "db"));
    }

    [Fact]
    public void EmptyFolder_ProducesEmptySchemaAndNoWarnings()
    {
        using var t = new TempFolder();
        var result = Reader.ReadFolder(t.Path, "srv", "MyProject");

        Assert.Empty(result.Schema.Tables);
        Assert.Empty(result.Schema.Views);
        Assert.Empty(result.Schema.StoredProcedures);
        Assert.Empty(result.Schema.Functions);
        Assert.Empty(result.Schema.Sequences);
        Assert.Empty(result.Schema.Synonyms);
        Assert.Empty(result.Schema.UserDefinedDataTypes);
        Assert.Empty(result.Schema.UserDefinedTableTypes);
        Assert.Empty(result.Schema.Schemas);
        Assert.Empty(result.Context.ObjectToFile);
        Assert.Empty(result.Context.ParseWarnings);
        Assert.Equal(t.Path, result.Context.FolderPath);
        Assert.Equal("srv", result.Schema.ServerName);
        Assert.Equal("MyProject", result.Schema.DatabaseName);
    }

    [Fact]
    public void SingleProcFile_AppearsInProcedureBucket()
    {
        using var t = new TempFolder();
        var path = t.WriteFile("PROC.dbo.GetOrders.sql",
            "CREATE PROC dbo.GetOrders AS SELECT 1");

        var result = Reader.ReadFolder(t.Path, "srv", "db");

        var proc = Assert.Single(result.Schema.StoredProcedures);
        Assert.Equal("GetOrders", proc.Name);
        Assert.Equal("dbo", proc.Schema);

        var backing = result.Context.ObjectToFile[proc.Id];
        Assert.Equal(path, backing.FilePath);
        Assert.True(backing.IsSingleObjectFile);
    }

    [Fact]
    public void MultipleFiles_EachWithOneObject_AllSingleObjectBacked()
    {
        using var t = new TempFolder();
        t.WriteFile("PROC.dbo.A.sql", "CREATE PROC dbo.A AS SELECT 1");
        t.WriteFile("VIEW.dbo.B.sql", "CREATE VIEW dbo.B AS SELECT 1");
        t.WriteFile("FUNC.dbo.C.sql", "CREATE FUNCTION dbo.C() RETURNS INT AS BEGIN RETURN 1 END");

        var result = Reader.ReadFolder(t.Path, "srv", "db");

        Assert.Single(result.Schema.StoredProcedures);
        Assert.Single(result.Schema.Views);
        Assert.Single(result.Schema.Functions);
        Assert.Equal(3, result.Context.ObjectToFile.Count);
        Assert.All(result.Context.ObjectToFile.Values, b => Assert.True(b.IsSingleObjectFile));
    }

    [Fact]
    public void FileWithMultipleObjects_AllMarkedAsMultiObject()
    {
        using var t = new TempFolder();
        var path = t.WriteFile("helpers.sql",
            "CREATE PROC dbo.Helper1 AS SELECT 1\nGO\n" +
            "CREATE PROC dbo.Helper2 AS SELECT 2\nGO\n");

        var result = Reader.ReadFolder(t.Path, "srv", "db");

        Assert.Equal(2, result.Schema.StoredProcedures.Count);
        Assert.All(result.Context.ObjectToFile.Values, b =>
        {
            Assert.False(b.IsSingleObjectFile);
            Assert.Equal(path, b.FilePath);
        });
    }

    [Fact]
    public void FileWithNoCreate_NoObjectsAddedAndNoWarning()
    {
        using var t = new TempFolder();
        t.WriteFile("readme.sql", "-- This file is documentation only.\nUSE [Foo];\nGO\n");

        var result = Reader.ReadFolder(t.Path, "srv", "db");

        Assert.Empty(result.Schema.StoredProcedures);
        Assert.Empty(result.Context.ParseWarnings);
    }

    [Fact]
    public void NonSqlFile_Ignored()
    {
        using var t = new TempFolder();
        t.WriteFile("PROC.dbo.A.sql", "CREATE PROC dbo.A AS SELECT 1");
        t.WriteFile("readme.txt", "CREATE PROC dbo.NotMe AS SELECT 1");
        t.WriteFile("notes.md", "# CREATE PROC dbo.AlsoNotMe");

        var result = Reader.ReadFolder(t.Path, "srv", "db");

        var proc = Assert.Single(result.Schema.StoredProcedures);
        Assert.Equal("A", proc.Name);
    }

    [Fact]
    public void SubfolderSqlFile_Ignored_RootOnlyInV12()
    {
        using var t = new TempFolder();
        t.WriteFile("PROC.dbo.Root.sql", "CREATE PROC dbo.Root AS SELECT 1");
        t.WriteFile(Path.Combine("nested", "PROC.dbo.Nested.sql"),
            "CREATE PROC dbo.Nested AS SELECT 1");

        var result = Reader.ReadFolder(t.Path, "srv", "db");

        var proc = Assert.Single(result.Schema.StoredProcedures);
        Assert.Equal("Root", proc.Name);
    }

    [Fact]
    public void DuplicateObjectAcrossFiles_WarningEmittedAndLastWins()
    {
        using var t = new TempFolder();
        // Two separate files defining dbo.GetOrders. The reader visits files
        // in filesystem order; ensure we collapse to one object and warn.
        t.WriteFile("a-PROC.dbo.GetOrders.sql", "CREATE PROC dbo.GetOrders AS SELECT 1");
        t.WriteFile("b-PROC.dbo.GetOrders.sql", "CREATE PROC dbo.GetOrders AS SELECT 999");

        var result = Reader.ReadFolder(t.Path, "srv", "db");

        var proc = Assert.Single(result.Schema.StoredProcedures);
        var warning = Assert.Single(result.Context.ParseWarnings);
        Assert.Contains("Duplicate object", warning);
        Assert.Contains("[dbo].[GetOrders]", warning);
    }

    [Fact]
    public void ObjectTypesAreRoutedToTheirBuckets()
    {
        using var t = new TempFolder();
        t.WriteFile("everything.sql",
            "CREATE PROC dbo.A AS SELECT 1\nGO\n" +
            "CREATE FUNCTION dbo.B() RETURNS INT AS BEGIN RETURN 1 END\nGO\n" +
            "CREATE VIEW dbo.C AS SELECT 1\nGO\n" +
            "CREATE TABLE dbo.D (Id INT)\nGO\n" +
            "CREATE SEQUENCE dbo.E AS BIGINT START WITH 1 INCREMENT BY 1\nGO\n" +
            "CREATE SYNONYM dbo.F FOR dbo.D\nGO\n" +
            "CREATE TYPE dbo.G FROM INT NOT NULL\nGO\n" +
            "CREATE TYPE dbo.H AS TABLE (Id INT)\nGO\n" +
            "CREATE SCHEMA audit\nGO\n");

        var result = Reader.ReadFolder(t.Path, "srv", "db");

        Assert.Single(result.Schema.StoredProcedures);
        Assert.Single(result.Schema.Functions);
        Assert.Single(result.Schema.Views);
        Assert.Single(result.Schema.Tables);
        Assert.Single(result.Schema.Sequences);
        Assert.Single(result.Schema.Synonyms);
        Assert.Single(result.Schema.UserDefinedDataTypes);
        Assert.Single(result.Schema.UserDefinedTableTypes);
        Assert.Single(result.Schema.Schemas);
    }

    [Fact]
    public void TableModel_HasEmptyStructuralFieldsInFolderMode()
    {
        // Folder mode is DDL-text-only; columns/indexes/etc. must be empty
        // so the comparator's column-level diff produces no spurious changes.
        using var t = new TempFolder();
        t.WriteFile("TABLE.dbo.Orders.sql", "CREATE TABLE dbo.Orders (Id INT NOT NULL, Total DECIMAL(10,2))");

        var result = Reader.ReadFolder(t.Path, "srv", "db");

        var table = Assert.Single(result.Schema.Tables);
        Assert.Empty(table.Columns);
        Assert.Empty(table.Indexes);
        Assert.Empty(table.ForeignKeys);
        Assert.Empty(table.CheckConstraints);
        Assert.Empty(table.Triggers);
    }

    [Fact]
    public void ReadFolderByDatabase_NoUseStatements_AllRouteToDefaultDb()
    {
        using var t = new TempFolder();
        t.WriteFile("PROC.dbo.A.sql", "CREATE PROC dbo.A AS SELECT 1");
        t.WriteFile("PROC.dbo.B.sql", "CREATE PROC dbo.B AS SELECT 2");

        var result = Reader.ReadFolderByDatabase(t.Path, "srv", defaultDatabase: "MyDb");

        var entry = Assert.Single(result);
        Assert.Equal("MyDb", entry.Key);
        Assert.Equal(2, entry.Value.Schema.StoredProcedures.Count);
    }

    [Fact]
    public void ReadFolderByDatabase_PerFileUse_RoutesToDeclaredDb()
    {
        using var t = new TempFolder();
        t.WriteFile("orders.sql",
            "USE [OrdersDb]\nGO\n" +
            "CREATE PROC dbo.GetOrders AS SELECT 1\n");
        t.WriteFile("lookup.sql",
            "USE [LookupDb]\nGO\n" +
            "CREATE PROC dbo.GetLookup AS SELECT 1\n");

        var result = Reader.ReadFolderByDatabase(t.Path, "srv", defaultDatabase: "DefaultDb");

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("OrdersDb"));
        Assert.True(result.ContainsKey("LookupDb"));
        Assert.False(result.ContainsKey("DefaultDb"));

        Assert.Equal("GetOrders", Assert.Single(result["OrdersDb"].Schema.StoredProcedures).Name);
        Assert.Equal("GetLookup", Assert.Single(result["LookupDb"].Schema.StoredProcedures).Name);
    }

    [Fact]
    public void ReadFolderByDatabase_MixedFiles_GroupCorrectly()
    {
        using var t = new TempFolder();
        // File with USE.
        t.WriteFile("orders.sql",
            "USE [OrdersDb]\nGO\nCREATE PROC dbo.GetOrders AS SELECT 1\n");
        // File without USE → goes to default.
        t.WriteFile("default.sql",
            "CREATE PROC dbo.Default AS SELECT 1\n");

        var result = Reader.ReadFolderByDatabase(t.Path, "srv", defaultDatabase: "MainDb");

        Assert.Equal(2, result.Count);
        Assert.Equal("GetOrders", Assert.Single(result["OrdersDb"].Schema.StoredProcedures).Name);
        Assert.Equal("Default", Assert.Single(result["MainDb"].Schema.StoredProcedures).Name);
    }

    [Fact]
    public void ReadFolderByDatabase_OneFileMultipleUses_SplitsObjectsAcrossDbs()
    {
        // Multi-object file with two USE statements, each routing one object.
        using var t = new TempFolder();
        var path = t.WriteFile("multi.sql",
            "USE [OrdersDb]\nGO\n" +
            "CREATE PROC dbo.GetOrders AS SELECT 1\nGO\n" +
            "USE [LookupDb]\nGO\n" +
            "CREATE PROC dbo.GetLookup AS SELECT 2\nGO\n");

        var result = Reader.ReadFolderByDatabase(t.Path, "srv", defaultDatabase: "DefaultDb");

        Assert.Equal(2, result.Count);
        var ordersBacking = result["OrdersDb"].Context.ObjectToFile.Values.Single();
        var lookupBacking = result["LookupDb"].Context.ObjectToFile.Values.Single();
        Assert.Equal(path, ordersBacking.FilePath);
        Assert.Equal(path, lookupBacking.FilePath);
        // The same file is shared across two DBs — neither side considers it
        // a single-object file because the source file holds two objects.
        Assert.False(ordersBacking.IsSingleObjectFile);
        Assert.False(lookupBacking.IsSingleObjectFile);
    }

    [Fact]
    public void ReadFolderByDatabase_EmptyFolder_ReturnsEmptyDictionary()
    {
        using var t = new TempFolder();
        var result = Reader.ReadFolderByDatabase(t.Path, "srv", defaultDatabase: "DefaultDb");
        Assert.Empty(result);
    }

    [Fact]
    public void ReadFolderByDatabase_OverruledUseWarning_AppearsInTargetDbBucket()
    {
        // Single-object file with USEs A then B before the CREATE → warning,
        // last-USE wins. The warning should surface on the DB the object
        // actually routed to (B), not the overruled one (A).
        using var t = new TempFolder();
        t.WriteFile("misc.sql",
            "USE [DbA]\nGO\nUSE [DbB]\nGO\nCREATE PROC dbo.X AS SELECT 1\n");

        var result = Reader.ReadFolderByDatabase(t.Path, "srv", defaultDatabase: "DefaultDb");

        Assert.Single(result);
        Assert.True(result.ContainsKey("DbB"));
        Assert.False(result.ContainsKey("DbA"));   // overruled, not a bucket
        var warning = Assert.Single(result["DbB"].Context.ParseWarnings);
        Assert.Contains("DbA", warning);
        Assert.Contains("DbB", warning);
    }

    [Fact]
    public void DdlOnObject_PreservesSourceTextIncludingHeaderComments()
    {
        using var t = new TempFolder();
        t.WriteFile("PROC.dbo.A.sql",
            "-- Header comment 1\n" +
            "-- Header comment 2\n" +
            "CREATE PROC dbo.A AS SELECT 1\n");

        var result = Reader.ReadFolder(t.Path, "srv", "db");
        var proc = Assert.Single(result.Schema.StoredProcedures);
        Assert.Contains("-- Header comment 1", proc.Ddl);
        Assert.Contains("-- Header comment 2", proc.Ddl);
        Assert.Contains("CREATE PROC dbo.A", proc.Ddl);
    }
}
