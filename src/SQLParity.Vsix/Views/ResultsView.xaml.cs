using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
                LoadAndShowDdl();
            }

            if (e.PropertyName == nameof(ResultsViewModel.SelectedDdlA)
                || e.PropertyName == nameof(ResultsViewModel.SelectedDdlB))
                UpdateDdlDiff();
        }

        private async void LoadAndShowDdl()
        {
            var vm = DataContext as ResultsViewModel;
            if (vm == null) return;

            // Show whatever DDL is already cached (instant for non-tables)
            UpdateDdlDiff();

            // Kick off async load for any missing table DDL
            var prevCursor = Mouse.OverrideCursor;
            try
            {
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                await vm.LoadDdlAsync();
            }
            finally
            {
                Mouse.OverrideCursor = prevCursor;
            }
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
                DdlBoxA.Document = new FlowDocument();
                DdlBoxB.Document = new FlowDocument();
                return;
            }

            // Read line number setting from options
            try
            {
                var opts = Options.OptionsHelper.GetOptions();
                if (opts != null)
                    SimpleDiffHighlighter.ShowLineNumbers = opts.ShowLineNumbers;
            }
            catch { }

            // Use SelectedDdlA/B which lazy-loads table DDL on demand
            SimpleDiffHighlighter.CreateAlignedDiffDocuments(
                vm.SelectedDdlA,
                vm.SelectedDdlB,
                out var docA, out var docB);
            DdlBoxA.Document = docA;
            DdlBoxB.Document = docB;

            // Hook sync scrolling now that the panels have content and are visible
            EnsureSyncScrollingHooked();
        }
    }
}
