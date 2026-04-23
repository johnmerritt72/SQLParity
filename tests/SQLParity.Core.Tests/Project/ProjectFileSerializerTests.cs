using System;
using System.IO;
using System.Collections.Generic;
using SQLParity.Core.Model;
using SQLParity.Core.Project;
using Xunit;

namespace SQLParity.Core.Tests.Project;

public class ProjectFileSerializerTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectFileSerializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SQLParity_Test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }

    private static ProjectFile MakeProject() => new()
    {
        LastSavedUtc = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc),
        SideA = new ConnectionSide
        {
            ServerName = "PROD-SERVER",
            DatabaseName = "MyDb",
            Label = "PROD",
            Tag = EnvironmentTag.Prod,
        },
        SideB = new ConnectionSide
        {
            ServerName = "DEV-SERVER",
            DatabaseName = "MyDb_Dev",
            Label = "DEV-Jane",
            Tag = EnvironmentTag.Dev,
        },
        Filters = new FilterSettings
        {
            IncludedObjectTypes = new[] { ObjectType.Table, ObjectType.View },
            ExcludedSchemas = new[] { "audit" },
            ExcludedNamePatterns = new[] { "tmp_*" },
        },
        IgnoredDifferences = new[]
        {
            new IgnoredDifference
            {
                ObjectId = "[dbo].[LegacyTable]",
                ObjectType = ObjectType.Table,
                Reason = "Intentionally different",
            },
        },
    };

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var path = Path.Combine(_tempDir, "test.sqlparity");
        var original = MakeProject();
        ProjectFileSerializer.Save(original, path);
        var loaded = ProjectFileSerializer.Load(path);
        Assert.Equal(original.Version, loaded.Version);
        Assert.Equal(original.SideA.ServerName, loaded.SideA.ServerName);
        Assert.Equal(original.SideA.DatabaseName, loaded.SideA.DatabaseName);
        Assert.Equal(original.SideA.Label, loaded.SideA.Label);
        Assert.Equal(original.SideA.Tag, loaded.SideA.Tag);
        Assert.Equal(original.SideB.ServerName, loaded.SideB.ServerName);
        Assert.Equal(original.SideB.Label, loaded.SideB.Label);
        Assert.Equal(original.SideB.Tag, loaded.SideB.Tag);
    }

    [Fact]
    public void RoundTrip_PreservesFilters()
    {
        var path = Path.Combine(_tempDir, "test.sqlparity");
        ProjectFileSerializer.Save(MakeProject(), path);
        var loaded = ProjectFileSerializer.Load(path);
        Assert.Equal(2, loaded.Filters.IncludedObjectTypes.Count);
        Assert.Contains(ObjectType.Table, loaded.Filters.IncludedObjectTypes);
        Assert.Contains(ObjectType.View, loaded.Filters.IncludedObjectTypes);
        Assert.Single(loaded.Filters.ExcludedSchemas);
        Assert.Equal("audit", loaded.Filters.ExcludedSchemas[0]);
        Assert.Single(loaded.Filters.ExcludedNamePatterns);
        Assert.Equal("tmp_*", loaded.Filters.ExcludedNamePatterns[0]);
    }

    [Fact]
    public void RoundTrip_PreservesIgnoredDifferences()
    {
        var path = Path.Combine(_tempDir, "test.sqlparity");
        ProjectFileSerializer.Save(MakeProject(), path);
        var loaded = ProjectFileSerializer.Load(path);
        Assert.Single(loaded.IgnoredDifferences);
        Assert.Equal("[dbo].[LegacyTable]", loaded.IgnoredDifferences[0].ObjectId);
        Assert.Equal(ObjectType.Table, loaded.IgnoredDifferences[0].ObjectType);
        Assert.Equal("Intentionally different", loaded.IgnoredDifferences[0].Reason);
    }

    [Fact]
    public void Save_CreatesReadableJsonFile()
    {
        var path = Path.Combine(_tempDir, "test.sqlparity");
        ProjectFileSerializer.Save(MakeProject(), path);
        var json = File.ReadAllText(path);
        Assert.Contains("PROD-SERVER", json);
        Assert.Contains("DEV-Jane", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_ProducesIndentedJson()
    {
        var path = Path.Combine(_tempDir, "test.sqlparity");
        ProjectFileSerializer.Save(MakeProject(), path);
        var json = File.ReadAllText(path);
        Assert.Contains("\n", json);
        Assert.Contains("  ", json);
    }

    [Fact]
    public void Load_NonExistentFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => ProjectFileSerializer.Load(Path.Combine(_tempDir, "nope.sqlparity")));
    }

    [Fact]
    public void DefaultProject_HasEmptyFiltersAndIgnoredDiffs()
    {
        var path = Path.Combine(_tempDir, "minimal.sqlparity");
        var minimal = new ProjectFile
        {
            SideA = new ConnectionSide { ServerName = "S", DatabaseName = "D", Label = "L", Tag = EnvironmentTag.Untagged },
            SideB = new ConnectionSide { ServerName = "S2", DatabaseName = "D2", Label = "L2", Tag = EnvironmentTag.Untagged },
        };
        ProjectFileSerializer.Save(minimal, path);
        var loaded = ProjectFileSerializer.Load(path);
        Assert.Empty(loaded.Filters.IncludedObjectTypes);
        Assert.Empty(loaded.Filters.ExcludedSchemas);
        Assert.Empty(loaded.Filters.ExcludedNamePatterns);
        Assert.Empty(loaded.IgnoredDifferences);
    }
}
