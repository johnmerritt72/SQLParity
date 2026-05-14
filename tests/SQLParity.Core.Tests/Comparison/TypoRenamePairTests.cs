using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Comparison;

public class TypoRenamePairTests
{
    private static StoredProcedureModel Proc(string schema, string name, string ddl = "")
        => new()
        {
            Id = SchemaQualifiedName.TopLevel(schema, name),
            Schema = schema,
            Name = name,
            Ddl = ddl,
        };

    private static DatabaseSchema SchemaWith(string label, params StoredProcedureModel[] procs)
        => new()
        {
            ServerName = "S",
            DatabaseName = label,
            ReadAtUtc = System.DateTime.UtcNow,
            Schemas = System.Array.Empty<SchemaModel>(),
            Tables = System.Array.Empty<TableModel>(),
            Views = System.Array.Empty<ViewModel>(),
            StoredProcedures = procs.ToList(),
            Functions = System.Array.Empty<UserDefinedFunctionModel>(),
            Sequences = System.Array.Empty<SequenceModel>(),
            Synonyms = System.Array.Empty<SynonymModel>(),
            UserDefinedDataTypes = System.Array.Empty<UserDefinedDataTypeModel>(),
            UserDefinedTableTypes = System.Array.Empty<UserDefinedTableTypeModel>(),
        };

    [Fact]
    public void FolderDrop_WithFileNameMatchingDbOrphan_BothGetRenameCandidates()
    {
        // A = DB (has Foo). B = folder (has Fooo with FileName=Foo).
        var dbProc = Proc("dbo", "Foo", "CREATE PROCEDURE dbo.Foo AS SELECT 1");
        var folderProc = Proc("dbo", "Fooo", "CREATE PROCEDURE dbo.Fooo AS SELECT 1");
        var fileNames = new Dictionary<SchemaQualifiedName, string?>
        {
            [folderProc.Id] = "Foo",
        };

        var result = SchemaComparator.Compare(
            SchemaWith("A", dbProc),
            SchemaWith("B", folderProc),
            SchemaReadOptions.All,
            sideBFileNames: fileNames);

        var newChange = Assert.Single(result.Changes.Where(c => c.Status == ChangeStatus.New));
        var dropChange = Assert.Single(result.Changes.Where(c => c.Status == ChangeStatus.Dropped));
        Assert.Equal("Foo", newChange.Id.Name);
        Assert.Equal("Fooo", dropChange.Id.Name);
        Assert.Contains("Fooo", newChange.RenameCandidateNames);  // DB-side knows about the folder typo
        Assert.Contains("Foo", dropChange.RenameCandidateNames);  // folder-side knows about the DB counterpart
    }

    [Fact]
    public void FolderDrop_FileNameMatchesCreateName_NoCandidate()
    {
        // FileName == Id.Name means no typo — don't false-positive.
        var dbProc = Proc("dbo", "Foo");
        var folderProc = Proc("dbo", "Bar");
        var fileNames = new Dictionary<SchemaQualifiedName, string?>
        {
            [folderProc.Id] = "Bar",
        };

        var result = SchemaComparator.Compare(
            SchemaWith("A", dbProc),
            SchemaWith("B", folderProc),
            SchemaReadOptions.All,
            sideBFileNames: fileNames);

        foreach (var c in result.Changes)
            Assert.Empty(c.RenameCandidateNames);
    }

    [Fact]
    public void FolderDrop_NoMatchingDbOrphan_EmptyCandidates()
    {
        // Folder has Fooo (FileName=Foo) but DB has nothing.
        var folderProc = Proc("dbo", "Fooo");
        var fileNames = new Dictionary<SchemaQualifiedName, string?>
        {
            [folderProc.Id] = "Foo",
        };

        var result = SchemaComparator.Compare(
            SchemaWith("A"),
            SchemaWith("B", folderProc),
            SchemaReadOptions.All,
            sideBFileNames: fileNames);

        var drop = Assert.Single(result.Changes);
        Assert.Empty(drop.RenameCandidateNames);
    }

    [Fact]
    public void FolderDrop_FileNameNull_TreatedAsNoSignal()
    {
        // Multi-batch file → FileName=null → no candidate detection.
        var dbProc = Proc("dbo", "Foo");
        var folderProc = Proc("dbo", "Fooo");
        var fileNames = new Dictionary<SchemaQualifiedName, string?>
        {
            [folderProc.Id] = null,
        };

        var result = SchemaComparator.Compare(
            SchemaWith("A", dbProc),
            SchemaWith("B", folderProc),
            SchemaReadOptions.All,
            sideBFileNames: fileNames);

        foreach (var c in result.Changes)
            Assert.Empty(c.RenameCandidateNames);
    }

    [Fact]
    public void DifferentSchema_DoesNotPair()
    {
        // FileName matches a DB orphan name, but in a different schema → no pair.
        var dbProc = Proc("sales", "Foo");
        var folderProc = Proc("dbo", "Fooo");
        var fileNames = new Dictionary<SchemaQualifiedName, string?>
        {
            [folderProc.Id] = "Foo",
        };

        var result = SchemaComparator.Compare(
            SchemaWith("A", dbProc),
            SchemaWith("B", folderProc),
            SchemaReadOptions.All,
            sideBFileNames: fileNames);

        foreach (var c in result.Changes)
            Assert.Empty(c.RenameCandidateNames);
    }

    [Fact]
    public void NoFileNamesDict_LiveVsLive_NoCandidates()
    {
        // sideBFileNames is null (live-vs-live or folder caller didn't pass).
        var dbProc = Proc("dbo", "Foo");
        var folderProc = Proc("dbo", "Fooo");

        var result = SchemaComparator.Compare(
            SchemaWith("A", dbProc),
            SchemaWith("B", folderProc),
            SchemaReadOptions.All,
            sideBFileNames: null);

        foreach (var c in result.Changes)
            Assert.Empty(c.RenameCandidateNames);
    }

    [Fact]
    public void LimitToFolderObjects_PreservesPairCandidateNew()
    {
        // The realistic folder-mode case: limitToFolderObjects=true normally
        // strips DB-only NEW changes. But a NEW that's a typo-rename pair
        // candidate should survive — otherwise the user can't pair from the
        // DB side and the DROP loses its hint partner.
        var dbProc = Proc("dbo", "Foo", "CREATE PROCEDURE dbo.Foo AS SELECT 1");
        var folderProc = Proc("dbo", "Fooo", "CREATE PROCEDURE dbo.Fooo AS SELECT 1");
        var unrelatedDbProc = Proc("dbo", "Unrelated", "CREATE PROCEDURE dbo.Unrelated AS SELECT 1");
        var fileNames = new Dictionary<SchemaQualifiedName, string?>
        {
            [folderProc.Id] = "Foo",  // file name matches dbProc — pair candidate
        };

        var result = SchemaComparator.Compare(
            SchemaWith("A", dbProc, unrelatedDbProc),
            SchemaWith("B", folderProc),
            SchemaReadOptions.All,
            limitToFolderObjects: true,
            sideBFileNames: fileNames);

        // Foo (DB orphan, pair candidate) survives
        var newChange = Assert.Single(result.Changes.Where(c => c.Status == ChangeStatus.New));
        Assert.Equal("Foo", newChange.Id.Name);
        Assert.Contains("Fooo", newChange.RenameCandidateNames);

        // Unrelated DB orphan was filtered out (no pair candidate)
        Assert.DoesNotContain(result.Changes, c => c.Id.Name == "Unrelated");

        // Folder DROP also has its pair candidate
        var dropChange = Assert.Single(result.Changes.Where(c => c.Status == ChangeStatus.Dropped));
        Assert.Contains("Foo", dropChange.RenameCandidateNames);
    }
}
