using System;
using Microsoft.VisualStudio;
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
        private static SolutionEventsListener _listener;
        private static IVsSolution _adviseSolution;
        private static uint _adviseCookie;

        /// <summary>
        /// Raised when SSMS opens or closes a solution / folder. Used by the
        /// connection-setup VM to enable / disable the Folder Mode radio
        /// button without requiring the user to reopen the SQLParity window.
        /// </summary>
        public static event EventHandler SolutionStateChanged;

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

        /// <summary>
        /// Idempotently subscribes to <see cref="IVsSolutionEvents"/> so the
        /// service raises <see cref="SolutionStateChanged"/> on open / close.
        /// Called automatically the first time anyone adds a handler.
        /// </summary>
        public static void EnsureSubscribed()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_listener != null) return;

            _adviseSolution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
            if (_adviseSolution == null) return;

            _listener = new SolutionEventsListener(() => SolutionStateChanged?.Invoke(null, EventArgs.Empty));
            _adviseSolution.AdviseSolutionEvents(_listener, out _adviseCookie);
        }

        /// <summary>
        /// Bare-bones IVsSolutionEvents implementation. Only the open/close
        /// callbacks do real work; everything else is a no-op so we can
        /// satisfy the interface without dragging in unrelated state.
        /// </summary>
        private sealed class SolutionEventsListener : IVsSolutionEvents
        {
            private readonly Action _onChange;
            public SolutionEventsListener(Action onChange) { _onChange = onChange; }

            public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
            { _onChange(); return VSConstants.S_OK; }

            public int OnAfterCloseSolution(object pUnkReserved)
            { _onChange(); return VSConstants.S_OK; }

            public int OnAfterCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
            public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
            public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
            public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
            public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;
            public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
            public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
            public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
            public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        }
    }
}
