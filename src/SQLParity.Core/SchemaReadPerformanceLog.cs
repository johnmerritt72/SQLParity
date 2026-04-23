using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SQLParity.Core
{
    /// <summary>
    /// Logs per-object-type timing during schema reads. Writes to %TEMP%\SQLParity_perf.log.
    /// </summary>
    public sealed class SchemaReadPerformanceLog
    {
        private readonly string _databaseName;
        private readonly List<string> _entries = new List<string>();
        private readonly Stopwatch _totalStopwatch = Stopwatch.StartNew();

        public SchemaReadPerformanceLog(string databaseName)
        {
            _databaseName = databaseName;
        }

        public void LogSection(string section, int objectCount, TimeSpan elapsed)
        {
            var avgMs = objectCount > 0 ? elapsed.TotalMilliseconds / objectCount : 0;
            var entry = $"  {section,-30} {objectCount,5} objects  {elapsed.TotalSeconds,8:F1}s  ({avgMs:F0}ms/obj)";
            _entries.Add(entry);
        }

        public void Finish()
        {
            _totalStopwatch.Stop();

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Schema read: {_databaseName}  (total: {_totalStopwatch.Elapsed.TotalSeconds:F1}s)");
            foreach (var entry in _entries)
                sb.AppendLine(entry);
            sb.AppendLine();

            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "SQLParity_perf.log");
                File.AppendAllText(logPath, sb.ToString());
            }
            catch { }
        }
    }
}
