using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using SQLParity.Vsix.Options;

namespace SQLParity.Vsix.Helpers
{
    /// <summary>
    /// Launches an external diff tool to show a side-by-side diff of two DDL strings.
    /// Uses the path and arguments configured in Tools > Options > SQLParity > General,
    /// falling back to auto-detected WinMerge when no custom tool is configured.
    /// </summary>
    public static class ExternalDiffLauncher
    {
        private static readonly string[] WinMergePaths =
        {
            @"C:\Program Files\WinMerge\WinMergeU.exe",
            @"C:\Program Files (x86)\WinMerge\WinMergeU.exe",
        };

        /// <summary>
        /// Writes two DDL strings to temp files and opens them in the configured diff tool.
        /// Returns true if the tool was launched, false if not found.
        /// </summary>
        public static bool TryLaunch(string ddlA, string ddlB, string labelA, string labelB)
        {
            string toolPath = null;
            string argsTemplate = null;

            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var opts = SQLParityPackage.Instance?.GetDialogPage(typeof(SQLParityOptionsPage)) as SQLParityOptionsPage;
                if (opts != null && !string.IsNullOrWhiteSpace(opts.DiffToolPath))
                {
                    toolPath = opts.DiffToolPath;
                    argsTemplate = opts.DiffToolArguments;
                }
            }
            catch { }

            // Fallback: auto-detect WinMerge
            if (string.IsNullOrEmpty(toolPath))
                toolPath = FindWinMerge();

            if (string.IsNullOrEmpty(toolPath))
            {
                MessageBox.Show(
                    "No external diff tool was found.\n\n" +
                    "Either configure a diff tool in Tools > Options > SQLParity > General,\n" +
                    "or install WinMerge from https://winmerge.org/ and try again.",
                    "SQLParity - Diff Tool Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrEmpty(argsTemplate))
                argsTemplate = "\"{leftFile}\" \"{rightFile}\" /dl \"{leftLabel}\" /dr \"{rightLabel}\" /e /u";

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "SQLParity_Diff");
                Directory.CreateDirectory(tempDir);

                string fileA = Path.Combine(tempDir, SanitizeFileName(labelA) + "_A.sql");
                string fileB = Path.Combine(tempDir, SanitizeFileName(labelB) + "_B.sql");

                File.WriteAllText(fileA, ddlA ?? string.Empty);
                File.WriteAllText(fileB, ddlB ?? string.Empty);

                string arguments = argsTemplate
                    .Replace("{leftFile}", fileA)
                    .Replace("{rightFile}", fileB)
                    .Replace("{leftLabel}", labelA ?? "Side A")
                    .Replace("{rightLabel}", labelB ?? "Side B");

                Process.Start(toolPath, arguments);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to launch diff tool:\n\n" + ex.Message,
                    "SQLParity - Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private static string FindWinMerge()
        {
            foreach (var path in WinMergePaths)
            {
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "side";

            char[] invalid = Path.GetInvalidFileNameChars();
            var sanitized = new char[name.Length];
            for (int i = 0; i < name.Length; i++)
            {
                sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
            }
            return new string(sanitized);
        }
    }
}
