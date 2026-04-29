using System.ComponentModel;
using System.Drawing.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms.Design;
using Microsoft.VisualStudio.Shell;

namespace SQLParity.Vsix.Options
{
    public enum AuthenticationMethod
    {
        [Description("Windows Authentication")]
        WindowsAuthentication,

        [Description("SQL Server Authentication")]
        SqlServerAuthentication
    }

    [Guid("E7F3A2B1-C4D5-4E6F-8A9B-0C1D2E3F4A5B")]
    internal class SQLParityOptionsPage : DialogPage
    {
        [Category("About")]
        [DisplayName("Version")]
        [Description("Installed SQLParity extension version.")]
        [ReadOnly(true)]
        public string Version => VersionInfo.Version;

        [Category("Connection Defaults")]
        [DisplayName("Default Authentication")]
        [Description("Default authentication method for new connections.")]
        [DefaultValue(AuthenticationMethod.WindowsAuthentication)]
        public AuthenticationMethod DefaultAuthentication { get; set; } = AuthenticationMethod.WindowsAuthentication;

        [Category("Connection Defaults")]
        [DisplayName("Default SQL Username")]
        [Description("Default SQL Server username (used when SQL Server Authentication is selected).")]
        [DefaultValue("")]
        public string DefaultSqlLogin { get; set; } = string.Empty;

        [Category("Connection Defaults")]
        [DisplayName("Save Passwords")]
        [Description("Save encrypted passwords (DPAPI) with connection history. Disable if your security policy prohibits stored credentials.")]
        [DefaultValue(true)]
        public bool SavePasswords { get; set; } = true;

        [Category("Comparison")]
        [DisplayName("Expand Tree by Default")]
        [Description("Automatically expand all nodes in the comparison tree.")]
        [DefaultValue(true)]
        public bool ExpandTreeByDefault { get; set; } = true;

        [Category("Comparison")]
        [DisplayName("Skip Confirmation Page")]
        [Description("Skip the confirmation screen and go directly to comparison after setting up connections.")]
        [DefaultValue(false)]
        public bool SkipConfirmationPage { get; set; } = false;

        [Category("Comparison")]
        [DisplayName("Show Line Numbers")]
        [Description("Show line numbers in the DDL diff panels.")]
        [DefaultValue(true)]
        public bool ShowLineNumbers { get; set; } = true;

        [Category("Comparison")]
        [DisplayName("Ignore Comments in Stored Procedures and Functions")]
        [Description("When enabled, stored procedures and user-defined functions whose only differences are inside SQL comments (-- ... or /* ... */) are not reported as Modified.")]
        [DefaultValue(false)]
        public bool IgnoreCommentsInStoredProcedures { get; set; } = false;

        [Category("Comparison")]
        [DisplayName("Ignore Whitespace in Stored Procedures and Functions")]
        [Description("When enabled, stored procedures and user-defined functions whose only differences are whitespace outside of string literals are not reported as Modified. Blank lines are also ignored. Whitespace inside 'string literals' is preserved exactly, since it can change behavior.")]
        [DefaultValue(false)]
        public bool IgnoreWhitespaceInStoredProcedures { get; set; } = false;

        [Category("Comparison")]
        [DisplayName("Ignore Optional Square Brackets")]
        [Description("When enabled, square brackets around regular identifiers are stripped before comparing any object's DDL (tables, views, procedures, functions, etc.). For example, [dbo].[TestProc] and dbo.TestProc are treated as identical. Brackets are kept whenever they are required: around reserved keywords (e.g. [Order]), names with special characters or spaces, or names starting with a digit.")]
        [DefaultValue(false)]
        public bool IgnoreOptionalBrackets { get; set; } = false;

        [Category("Comparison")]
        [DisplayName("Limit Comparison to Solution Folder Objects")]
        [Description("Only takes effect when Side B is set to Folder mode. When enabled, objects that exist in the live database but are not represented by a .sql file in the solution folder are hidden from the change list. An empty folder always shows everything from Side A regardless of this setting.")]
        [DefaultValue(true)]
        public bool LimitComparisonToFolderObjects { get; set; } = true;

        [Category("Performance")]
        [DisplayName("Schema Cache TTL (minutes)")]
        [Description("How long to cache schema reads in memory. Set to 0 to disable caching.")]
        [DefaultValue(5)]
        public int SchemaCacheTtlMinutes { get; set; } = 5;

        [Category("External Diff Tool")]
        [DisplayName("Diff Tool Path")]
        [Description("Path to the external diff tool executable.")]
        [Editor(typeof(FileNameEditor), typeof(UITypeEditor))]
        public string DiffToolPath { get; set; } = DetectWinMergePath();

        private static string DetectWinMergePath()
        {
            var paths = new[]
            {
                @"C:\Program Files\WinMerge\WinMergeU.exe",
                @"C:\Program Files (x86)\WinMerge\WinMergeU.exe",
            };
            foreach (var p in paths)
            {
                if (File.Exists(p)) return p;
            }
            return string.Empty;
        }

        [Category("External Diff Tool")]
        [DisplayName("Diff Tool Arguments")]
        [Description("Arguments template. Tokens: {leftFile}, {rightFile}, {leftLabel}, {rightLabel}")]
        [DefaultValue("\"{leftFile}\" \"{rightFile}\" /dl \"{leftLabel}\" /dr \"{rightLabel}\" /e /u")]
        public string DiffToolArguments { get; set; } = "\"{leftFile}\" \"{rightFile}\" /dl \"{leftLabel}\" /dr \"{rightLabel}\" /e /u";
    }
}
