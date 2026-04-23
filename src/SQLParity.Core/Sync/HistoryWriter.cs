using System;
using System.IO;
using System.Text;

namespace SQLParity.Core.Sync;

public sealed class HistoryWriter
{
    private readonly string _historyFolder;

    public HistoryWriter(string historyFolder)
    {
        _historyFolder = historyFolder ?? throw new ArgumentNullException(nameof(historyFolder));
    }

    public string SaveScript(SyncScript script)
    {
        EnsureDirectoryExists();
        var timestamp = script.GeneratedAtUtc.ToString("yyyy-MM-dd_HHmmss");
        var fileName = $"script_{timestamp}_{script.DestinationDatabase}.sql";
        var path = Path.Combine(_historyFolder, fileName);
        File.WriteAllText(path, script.SqlText, Encoding.UTF8);
        return path;
    }

    public string SaveApplyResult(ApplyResult result)
    {
        EnsureDirectoryExists();
        var timestamp = result.StartedAtUtc.ToString("yyyy-MM-dd_HHmmss");
        var fileName = $"apply_{timestamp}_{result.DestinationDatabase}.txt";
        var path = Path.Combine(_historyFolder, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("SQLParity — Live Apply Result");
        sb.AppendLine("========================================");
        sb.AppendLine($"Started:      {result.StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Completed:    {result.CompletedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Destination:  {result.DestinationServer} / {result.DestinationDatabase}");
        sb.AppendLine($"Succeeded:    {result.SucceededCount}");
        sb.AppendLine($"Failed:       {result.FailedCount}");
        sb.AppendLine($"Fully OK:     {result.FullySucceeded}");
        sb.AppendLine("========================================");
        sb.AppendLine();

        foreach (var step in result.Steps)
        {
            var status = step.Succeeded ? "Succeeded" : "FAILED";
            sb.AppendLine($"[{status}] {step.ObjectName} ({step.Duration.TotalMilliseconds:F0}ms)");
            if (!step.Succeeded && step.ErrorMessage is not null)
                sb.AppendLine($"  Error: {step.ErrorMessage}");
            sb.AppendLine($"  SQL: {step.Sql}");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_historyFolder))
            Directory.CreateDirectory(_historyFolder);
    }
}
