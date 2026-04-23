using System;
using System.IO;
using SQLParity.Core.Model;
using SQLParity.Core.Project;
using Xunit;

namespace SQLParity.Core.Tests.Project;

public class EnvironmentTagStoreTests : IDisposable
{
    private readonly string _tempFile;

    public EnvironmentTagStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), "SQLParity_Test_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_tags.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetTag_UnknownServer_ReturnsUntagged()
    {
        var store = new EnvironmentTagStore(_tempFile);
        Assert.Equal(EnvironmentTag.Untagged, store.GetTag("Unknown", "Db"));
    }

    [Fact]
    public void SetAndGetTag_Persists()
    {
        var store = new EnvironmentTagStore(_tempFile);
        store.SetTag("Server1", "MyDb", EnvironmentTag.Prod);
        Assert.Equal(EnvironmentTag.Prod, store.GetTag("Server1", "MyDb"));
    }

    [Fact]
    public void SetTag_PersistsAcrossInstances()
    {
        var store1 = new EnvironmentTagStore(_tempFile);
        store1.SetTag("Server1", "MyDb", EnvironmentTag.Staging);
        var store2 = new EnvironmentTagStore(_tempFile);
        Assert.Equal(EnvironmentTag.Staging, store2.GetTag("Server1", "MyDb"));
    }

    [Fact]
    public void SetTag_OverwritesPrevious()
    {
        var store = new EnvironmentTagStore(_tempFile);
        store.SetTag("Server1", "MyDb", EnvironmentTag.Dev);
        store.SetTag("Server1", "MyDb", EnvironmentTag.Prod);
        Assert.Equal(EnvironmentTag.Prod, store.GetTag("Server1", "MyDb"));
    }

    [Fact]
    public void GetTag_IsCaseInsensitive()
    {
        var store = new EnvironmentTagStore(_tempFile);
        store.SetTag("SERVER1", "MYDB", EnvironmentTag.Prod);
        Assert.Equal(EnvironmentTag.Prod, store.GetTag("server1", "mydb"));
    }

    [Fact]
    public void MultipleTags_IndependentlyStored()
    {
        var store = new EnvironmentTagStore(_tempFile);
        store.SetTag("Server1", "ProdDb", EnvironmentTag.Prod);
        store.SetTag("Server1", "DevDb", EnvironmentTag.Dev);
        Assert.Equal(EnvironmentTag.Prod, store.GetTag("Server1", "ProdDb"));
        Assert.Equal(EnvironmentTag.Dev, store.GetTag("Server1", "DevDb"));
    }

    [Fact]
    public void SuggestTag_Prod()
    {
        Assert.Equal(EnvironmentTag.Prod, EnvironmentTagStore.SuggestTagFromLabel("PROD"));
        Assert.Equal(EnvironmentTag.Prod, EnvironmentTagStore.SuggestTagFromLabel("Production DB"));
        Assert.Equal(EnvironmentTag.Prod, EnvironmentTagStore.SuggestTagFromLabel("my-prod-server"));
    }

    [Fact]
    public void SuggestTag_Dev()
    {
        Assert.Equal(EnvironmentTag.Dev, EnvironmentTagStore.SuggestTagFromLabel("DEV"));
        Assert.Equal(EnvironmentTag.Dev, EnvironmentTagStore.SuggestTagFromLabel("DEV-Jane"));
        Assert.Equal(EnvironmentTag.Dev, EnvironmentTagStore.SuggestTagFromLabel("development"));
    }

    [Fact]
    public void SuggestTag_Staging()
    {
        Assert.Equal(EnvironmentTag.Staging, EnvironmentTagStore.SuggestTagFromLabel("STAGING"));
        Assert.Equal(EnvironmentTag.Staging, EnvironmentTagStore.SuggestTagFromLabel("STAGE-release-42"));
    }

    [Fact]
    public void SuggestTag_Sandbox()
    {
        Assert.Equal(EnvironmentTag.Sandbox, EnvironmentTagStore.SuggestTagFromLabel("SANDBOX"));
        Assert.Equal(EnvironmentTag.Sandbox, EnvironmentTagStore.SuggestTagFromLabel("my-sandbox"));
    }

    [Fact]
    public void SuggestTag_Unknown_ReturnsUntagged()
    {
        Assert.Equal(EnvironmentTag.Untagged, EnvironmentTagStore.SuggestTagFromLabel("MyDatabase"));
        Assert.Equal(EnvironmentTag.Untagged, EnvironmentTagStore.SuggestTagFromLabel("Orders"));
    }
}
