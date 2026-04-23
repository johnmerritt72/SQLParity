using System;
using System.IO;
using System.Collections.Generic;
using SQLParity.Core.Sync;
using Xunit;

namespace SQLParity.Core.Tests.Sync;

public class HistoryWriterTests : IDisposable
{
    private readonly string _tempDir;

    public HistoryWriterTests()
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

    [Fact]
    public void SaveScript_CreatesFileInHistoryFolder()
    {
        var writer = new HistoryWriter(_tempDir);
        var script = new SyncScript
        {
            SqlText = "USE [MyDb]\nGO\nCREATE TABLE ...",
            GeneratedAtUtc = new DateTime(2026, 4, 10, 14, 30, 0, DateTimeKind.Utc),
            DestinationDatabase = "MyDb",
            DestinationServer = "Server1",
            TotalChanges = 5,
            DestructiveChanges = 1,
        };

        var path = writer.SaveScript(script);

        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        Assert.Contains("CREATE TABLE", content);
    }

    [Fact]
    public void SaveScript_FilenameContainsTimestamp()
    {
        var writer = new HistoryWriter(_tempDir);
        var script = new SyncScript
        {
            SqlText = "SELECT 1",
            GeneratedAtUtc = new DateTime(2026, 4, 10, 14, 30, 0, DateTimeKind.Utc),
            DestinationDatabase = "MyDb",
            DestinationServer = "Server1",
            TotalChanges = 1,
            DestructiveChanges = 0,
        };

        var path = writer.SaveScript(script);

        Assert.Contains("2026", Path.GetFileName(path));
        Assert.EndsWith(".sql", path);
    }

    [Fact]
    public void SaveApplyResult_CreatesFile()
    {
        var writer = new HistoryWriter(_tempDir);
        var result = new ApplyResult
        {
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            CompletedAtUtc = DateTime.UtcNow,
            DestinationDatabase = "MyDb",
            DestinationServer = "Server1",
            Steps = new List<ApplyStepResult>
            {
                new ApplyStepResult
                {
                    ObjectName = "[dbo].[Orders]",
                    Sql = "CREATE TABLE ...",
                    Succeeded = true,
                    ErrorMessage = null,
                    Duration = TimeSpan.FromMilliseconds(42),
                },
            },
            FullySucceeded = true,
        };

        var path = writer.SaveApplyResult(result);

        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        Assert.Contains("dbo", content);
        Assert.Contains("Succeeded", content);
    }

    [Fact]
    public void HistoryFolder_CreatedIfNotExists()
    {
        var subDir = Path.Combine(_tempDir, "history");
        Assert.False(Directory.Exists(subDir));

        var writer = new HistoryWriter(subDir);
        var script = new SyncScript
        {
            SqlText = "SELECT 1",
            GeneratedAtUtc = DateTime.UtcNow,
            DestinationDatabase = "Db",
            DestinationServer = "Srv",
            TotalChanges = 0,
            DestructiveChanges = 0,
        };

        writer.SaveScript(script);

        Assert.True(Directory.Exists(subDir));
    }
}
