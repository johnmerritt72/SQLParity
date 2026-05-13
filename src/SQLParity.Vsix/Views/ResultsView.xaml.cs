using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SQLParity.Vsix.Helpers;
using SQLParity.Vsix.ViewModels;

namespace SQLParity.Vsix.Views
{
    public partial class ResultsView : UserControl
    {
        private bool _isSyncingScroll;
        private bool _scrollingHooked;

        public ResultsView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>
        /// Hooks sync scrolling after the DDL panels are visible and have content.
        /// Must be called after the detail panel becomes visible and documents are loaded.
        /// </summary>
        private void EnsureSyncScrollingHooked()
        {
            if (_scrollingHooked) return;

            // Defer to let WPF create the visual tree for the now-visible RichTextBoxes
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new System.Action(() =>
            {
                if (_scrollingHooked) return;

                var scrollA = FindScrollViewer(DdlBoxA);
                var scrollB = FindScrollViewer(DdlBoxB);

                if (scrollA != null && scrollB != null)
                {
                    scrollA.ScrollChanged += (s, args) =>
                    {
                        if (_isSyncingScroll) return;
                        _isSyncingScroll = true;
                        scrollB.ScrollToVerticalOffset(scrollA.VerticalOffset);
                        scrollB.ScrollToHorizontalOffset(scrollA.HorizontalOffset);
                        _isSyncingScroll = false;
                    };

                    scrollB.ScrollChanged += (s, args) =>
                    {
                        if (_isSyncingScroll) return;
                        _isSyncingScroll = true;
                        scrollA.ScrollToVerticalOffset(scrollB.VerticalOffset);
                        scrollA.ScrollToHorizontalOffset(scrollB.HorizontalOffset);
                        _isSyncingScroll = false;
                    };

                    _scrollingHooked = true;
                }
            }));
        }

        private static ScrollViewer FindScrollViewer(DependencyObject parent)
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer sv) return sv;
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ResultsViewModel oldVm)
            {
                oldVm.PropertyChanged -= Vm_PropertyChanged;
                if (oldVm.Direction != null)
                    oldVm.Direction.PropertyChanged -= Direction_PropertyChanged;
            }

            if (e.NewValue is ResultsViewModel newVm)
            {
                newVm.PropertyChanged += Vm_PropertyChanged;
                if (newVm.Direction != null)
                    newVm.Direction.PropertyChanged += Direction_PropertyChanged;
            }

            UpdateDetailVisibility();
            UpdateDdlHeaderColors();
            UpdateDdlDiff();
        }

        private void Vm_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ResultsViewModel.SelectedTreeItem))
            {
                UpdateDetailVisibility();
                UpdateDdlDiff();
            }

            if (e.PropertyName == nameof(ResultsViewModel.SelectedDdlA)
                || e.PropertyName == nameof(ResultsViewModel.SelectedDdlB))
                UpdateDdlDiff();
        }

        private void Direction_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Update colors whenever any Direction property changes (TagA, TagB, Direction, etc.)
            UpdateDdlHeaderColors();
        }

        private void ChangeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is ResultsViewModel vm)
                vm.SelectedTreeItem = e.NewValue as ChangeTreeItemViewModel;
        }

        private void OpenInWinMerge_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ResultsViewModel vm && vm.SelectedChange != null)
            {
                ExternalDiffLauncher.TryLaunch(
                    vm.SelectedDdlA,
                    vm.SelectedDdlB,
                    vm.Direction.LabelA,
                    vm.Direction.LabelB);
            }
        }

        private void UpdateDetailVisibility()
        {
            var hasSelection = (DataContext as ResultsViewModel)?.SelectedTreeItem != null;
            DetailContent.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            NoSelectionPlaceholder.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;

            // Delay color update until after WPF has rendered the now-visible elements
            if (hasSelection)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new System.Action(UpdateDdlHeaderColors));
            }
        }

        private void UpdateDdlHeaderColors()
        {
            var vm = DataContext as ResultsViewModel;
            if (vm == null) return;
            var brushA = EnvironmentTagColors.GetBrush(vm.Direction.TagA);
            var brushB = EnvironmentTagColors.GetBrush(vm.Direction.TagB);
            DdlHeaderA.Foreground = brushA;
            DdlHeaderB.Foreground = brushB;
            DdlBorderA.BorderBrush = brushA;
            DdlBorderB.BorderBrush = brushB;
        }

        private void UpdateDdlDiff()
        {
            var vm = DataContext as ResultsViewModel;
            if (vm?.SelectedChange == null)
            {
                ErrorBannerBorder.Visibility = Visibility.Collapsed;
                DdlBoxA.Document = new FlowDocument();
                DdlBoxB.Document = new FlowDocument();
                return;
            }

            try
            {
                var opts = Options.OptionsHelper.GetOptions();
                if (opts != null)
                    SimpleDiffHighlighter.ShowLineNumbers = opts.ShowLineNumbers;
            }
            catch { }

            var ddlA = vm.SelectedDdlA ?? string.Empty;
            var ddlB = vm.SelectedDdlB ?? string.Empty;
            var errorA = ExtractScriptError(ddlA);
            var errorB = ExtractScriptError(ddlB);

            if (errorA != null || errorB != null)
            {
                ErrorBannerBorder.Visibility = Visibility.Visible;
                ErrorBanner.Text = BuildBannerText(errorA, errorB, vm.Direction?.LabelA, vm.Direction?.LabelB);
                DdlBoxA.Document = errorA != null
                    ? PlaceholderDoc("(could not load — see error above)")
                    : RawDoc(ddlA);
                DdlBoxB.Document = errorB != null
                    ? PlaceholderDoc("(could not load — see error above)")
                    : RawDoc(ddlB);
                return;
            }

            ErrorBannerBorder.Visibility = Visibility.Collapsed;

            SimpleDiffHighlighter.CreateAlignedDiffDocuments(ddlA, ddlB, out var docA, out var docB);
            DdlBoxA.Document = docA;
            DdlBoxB.Document = docB;

            EnsureSyncScrollingHooked();
        }

        /// <summary>
        /// Detects the "-- Could not script ..." sentinel emitted by SchemaReader when SMO scripting fails
        /// (e.g. for views, procs, functions when VIEW DATABASE STATE is denied). Returns the human-readable
        /// detail after the sentinel prefix, or null if the DDL does not start with the sentinel.
        /// </summary>
        private static string? ExtractScriptError(string ddl)
        {
            if (string.IsNullOrEmpty(ddl)) return null;
            const string sentinel = "-- ";
            const string prefix = "-- Could not script ";
            if (!ddl.StartsWith(prefix, System.StringComparison.Ordinal)) return null;

            int newline = ddl.IndexOf('\n');
            var firstLine = newline < 0 ? ddl : ddl.Substring(0, newline).TrimEnd('\r');
            return firstLine.Substring(sentinel.Length);
        }

        private static string BuildBannerText(string? errorA, string? errorB, string? labelA, string? labelB)
        {
            var sb = new System.Text.StringBuilder();
            if (errorA != null)
                sb.Append("Could not load DDL for [").Append(labelA ?? "Side A").Append("]: ").Append(errorA);
            if (errorB != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("Could not load DDL for [").Append(labelB ?? "Side B").Append("]: ").Append(errorB);
            }
            return sb.ToString();
        }

        private static FlowDocument PlaceholderDoc(string text)
        {
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Consolas"),
                PagePadding = new Thickness(4),
            };
            var p = new Paragraph(new Run(text)) { Foreground = Brushes.Gray, FontStyle = FontStyles.Italic };
            doc.Blocks.Add(p);
            return doc;
        }

        private static FlowDocument RawDoc(string text)
        {
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Consolas"),
                PagePadding = new Thickness(4),
            };
            var p = new Paragraph(new Run(text ?? string.Empty));
            doc.Blocks.Add(p);
            return doc;
        }
    }
}
