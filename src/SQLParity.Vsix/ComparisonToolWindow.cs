using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SQLParity.Vsix
{
    [Guid("b2c3d4e5-f6a7-8901-bcde-f12345678901")]
    public sealed class ComparisonToolWindow : ToolWindowPane
    {
        public ComparisonToolWindow() : base(null)
        {
            this.Caption = "SQLParity - v" + VersionInfo.Version;
            this.Content = new Views.ComparisonHostView();
        }
    }
}
