using Microsoft.VisualStudio.Shell;

namespace SQLParity.Vsix.Options
{
    internal static class OptionsHelper
    {
        public static SQLParityOptionsPage GetOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var page = SQLParityPackage.Instance?.GetDialogPage(typeof(SQLParityOptionsPage)) as SQLParityOptionsPage;
            return page;
        }
    }
}
