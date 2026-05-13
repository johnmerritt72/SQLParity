using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SQLParity.Vsix
{
    /// <summary>
    /// Command that reads the currently selected database node in SSMS Object Explorer
    /// and opens the SQLParity tool window with Side A pre-filled.
    /// </summary>
    internal sealed class CompareWithCommand
    {
        public static readonly Guid CommandSet = new Guid("c3d4e5f6-a7b8-9012-cdef-123456789012");
        public const int CommandId = 0x0200;

        private readonly AsyncPackage _package;

        private CompareWithCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                new CompareWithCommand(package, commandService);
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string serverName = null;
            string databaseName = null;

            try
            {
                // Get the Object Explorer service from SSMS
                var oeServiceType = Type.GetType(
                    "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService, " +
                    "SqlWorkbench.Interfaces");

                if (oeServiceType != null)
                {
                    var oeService = ThreadHelper.JoinableTaskFactory.Run(
                        () => _package.GetServiceAsync(oeServiceType));

                    if (oeService != null)
                    {
                        // Use reflection to call GetSelectedNodes since we reference via Type.GetType
                        var method = oeServiceType.GetMethod("GetSelectedNodes");
                        if (method != null)
                        {
                            var args = new object[] { 0, null };
                            method.Invoke(oeService, args);
                            int count = (int)args[0];
                            var nodes = args[1] as Array;

                            if (count > 0 && nodes != null && nodes.Length > 0)
                            {
                                var node = nodes.GetValue(0);
                                var nodeType = node.GetType();

                                var iNodeInfoType = Type.GetType(
                                    "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.INodeInformation, " +
                                    "SqlWorkbench.Interfaces");

                                var nameProp = iNodeInfoType?.GetProperty("Name") ?? nodeType.GetProperty("Name");
                                var parentProp = iNodeInfoType?.GetProperty("Parent") ?? nodeType.GetProperty("Parent");

                                var nodeName = nameProp?.GetValue(node) as string;

                                // Determine what kind of node is selected by checking the parent chain:
                                // Server node: Parent is null (top-level)
                                // "Databases" folder: Parent is the server node
                                // Database node: Parent is "Databases" folder, GrandParent is server node
                                object parentNode = parentProp?.GetValue(node);
                                object grandParentNode = parentNode != null ? parentProp?.GetValue(parentNode) : null;

                                if (grandParentNode != null)
                                {
                                    // Node has a grandparent — this is a database node
                                    // Node = database, Parent = "Databases" folder, GrandParent = server
                                    databaseName = nodeName;
                                    serverName = nameProp?.GetValue(grandParentNode) as string;
                                }
                                else if (parentNode != null)
                                {
                                    // Node has a parent but no grandparent — could be "Databases" folder or server child
                                    // Treat the parent as the server node, leave database blank
                                    serverName = nameProp?.GetValue(parentNode) as string;
                                }
                                else
                                {
                                    // No parent — this is a server node itself
                                    serverName = nodeName;
                                }

                                // The server node Name in SSMS often looks like "SERVERNAME (SQL Server ...)"
                                // Strip the descriptive suffix if present.
                                if (!string.IsNullOrEmpty(serverName))
                                {
                                    var parenIndex = serverName.IndexOf(" (", StringComparison.Ordinal);
                                    if (parenIndex > 0)
                                    {
                                        serverName = serverName.Substring(0, parenIndex);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SQLParity: Could not read Object Explorer node: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(serverName) && string.IsNullOrWhiteSpace(databaseName))
            {
                System.Windows.MessageBox.Show(
                    "Please select a server or database node in Object Explorer first, then try again.",
                    "SQLParity — Compare Selected Database",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            // Open the tool window
            var window = _package.FindToolWindow(typeof(ComparisonToolWindow), 0, true);
            if (window?.Frame == null)
                throw new NotSupportedException("Cannot create tool window");

            // Pre-fill Side A from the selected node
            if (window.Content is Views.ComparisonHostView hostView
                && hostView.DataContext is ViewModels.ComparisonHostViewModel hostVm)
            {
                // Ensure we are on the ConnectionSetup screen
                hostVm.CurrentState = ViewModels.WorkflowState.ConnectionSetup;

                var sideA = hostVm.SetupViewModel.SideA;

                // If we have both server and database, prefer an exact saved
                // connection so we pick up credentials specific to this DB
                // rather than the server's most-recently-used row.
                Helpers.SavedConnection exactMatch = null;
                if (!string.IsNullOrWhiteSpace(serverName) && !string.IsNullOrWhiteSpace(databaseName))
                {
                    try
                    {
                        var store = new Helpers.ConnectionHistoryStore();
                        exactMatch = store.FindByServerAndDatabase(serverName, databaseName);
                    }
                    catch (Exception lookupEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "SQLParity: saved-connection lookup failed: " + lookupEx.Message);
                    }
                }

                if (exactMatch != null)
                {
                    sideA.LoadFromSavedConnection(exactMatch, serverName, databaseName);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(serverName))
                    {
                        sideA.ServerName = serverName;
                    }

                    sideA.DatabaseName = databaseName;
                }
            }

            var windowFrame = (IVsWindowFrame)window.Frame;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
        }
    }
}
