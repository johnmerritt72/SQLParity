using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SQLParity.Vsix.Helpers
{
    /// <summary>
    /// Thin wrapper around SSMS's solution APIs. Avoids EnvDTE in favor of the
    /// IVsSolution shell service, which is part of the same SDK we already
    /// reference. All methods must be called on the UI thread.
    /// </summary>
    public static class SsmsSolutionService
    {
        /// <summary>True when SSMS has a solution loaded with a saved .ssmssln file.</summary>
        public static bool IsSolutionOpen()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution sol))
                    return false;
                sol.GetSolutionInfo(out _, out string solutionFile, out _);
                return !string.IsNullOrEmpty(solutionFile);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the directory of the open solution, or null if no solution
        /// is loaded (or the solution is unsaved).
        /// </summary>
        public static string GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution sol))
                    return null;
                sol.GetSolutionInfo(out string solutionDir, out string solutionFile, out _);
                if (string.IsNullOrEmpty(solutionFile)) return null;
                return solutionDir;
            }
            catch
            {
                return null;
            }
        }
    }
}
