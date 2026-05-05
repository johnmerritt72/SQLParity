using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using SQLParity.Core.Comparison;
using SQLParity.Core.Model;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class ResultsViewModel : ViewModelBase
    {
        private ChangeTreeItemViewModel _selectedTreeItem;
        private ComparisonResult _comparisonResult;
        private string _toastMessage = string.Empty;
        private DispatcherTimer _toastTimer;
        private ObservableCollection<TableTreeNode> _tableTreeItems;
        private bool _isTableSelected;
        private bool _isLoadingDdl;
        private int _ddlLoadGeneration;
        private string _filterText = string.Empty;

        public ResultsViewModel()
        {
            Direction = new SyncDirectionViewModel();
            TreeItems = new ObservableCollection<ChangeTreeItemViewModel>();
            Direction.DirectionChanged += OnDirectionChanged;

            CheckAllCommand = new RelayCommand(_ => SetAllChecked(true));
            UncheckAllCommand = new RelayCommand(_ => SetAllChecked(false));
            UncheckDestructiveCommand = new RelayCommand(_ => SetCheckedByRisk(RiskTier.Destructive, false));
            UncheckRiskyCommand = new RelayCommand(_ => SetCheckedByRisk(RiskTier.Risky, false));
            CheckOnlySafeCommand = new RelayCommand(_ =>
            {
                foreach (var leaf in AllLeaves())
                    leaf.IsChecked = VisualRisk(leaf) == RiskTier.Safe;
                RefreshAllGroups();
                OnPropertyChanged(nameof(SummaryText));
            });
            ClearFilterCommand = new RelayCommand(_ => FilterText = string.Empty);
            ApplyRenameCommand = new RelayCommand(oldName => ApplyRename(oldName as string));
            UndoRenameCommand = new RelayCommand(newName => UndoRename(newName as string));
            UncheckExternalRefsCommand = new RelayCommand(_ =>
            {
                foreach (var leaf in AllLeaves())
                {
                    if (leaf.IsVisible && leaf.HasExternalReferences)
                        leaf.IsChecked = false;
                }
                RefreshAllGroups();
                OnPropertyChanged(nameof(SummaryText));
            });
        }

        public ICommand UncheckExternalRefsCommand { get; }

        /// <summary>
        /// Marks a NEW column and a DROPPED column as a rename, collapsing the
        /// two entries into a single <see cref="ChangeStatus.Renamed"/> entry.
        /// Called from the table detail tree.
        /// </summary>
        private void ApplyRename(string oldColumnName)
        {
            var change = SelectedChange;
            if (change == null || change.ColumnChanges == null || string.IsNullOrEmpty(oldColumnName))
                return;

            // The new column name is whichever NEW column triggered the action.
            // We find it by matching: the NEW column whose type matches the dropped
            // column with name = oldColumnName. If there's only one NEW candidate,
            // use it; otherwise we can't determine which — pick the first.
            var dropped = change.ColumnChanges.FirstOrDefault(c =>
                c.Status == ChangeStatus.Dropped
                && string.Equals(c.ColumnName, oldColumnName, StringComparison.OrdinalIgnoreCase));
            if (dropped == null) return;

            var added = change.ColumnChanges.FirstOrDefault(c =>
                c.Status == ChangeStatus.New
                && c.SideA != null
                && dropped.SideB != null
                && string.Equals(c.SideA.DataType, dropped.SideB.DataType, StringComparison.OrdinalIgnoreCase)
                && c.SideA.MaxLength == dropped.SideB.MaxLength
                && c.SideA.Precision == dropped.SideB.Precision
                && c.SideA.Scale == dropped.SideB.Scale
                && c.SideA.IsNullable == dropped.SideB.IsNullable);
            if (added == null) return;

            // Build the Renamed entry. SideA is the new column def, SideB is the old.
            var renamed = new ColumnChange
            {
                Id = added.Id,
                ColumnName = added.ColumnName,
                OldColumnName = dropped.ColumnName,
                Status = ChangeStatus.Renamed,
                SideA = added.SideA,
                SideB = dropped.SideB,
                Risk = RiskTier.Caution,
                Reasons = Array.Empty<RiskReason>(),
            };

            change.ColumnChanges.Remove(added);
            change.ColumnChanges.Remove(dropped);
            change.ColumnChanges.Add(renamed);

            RecomputeChangeRisk(change);
            UpdateTableTree();
            OnPropertyChanged(nameof(SummaryText));
        }

        /// <summary>
        /// Undoes a rename mapping, expanding the Renamed entry back into the
        /// original DROP and ADD pair.
        /// </summary>
        private void UndoRename(string newColumnName)
        {
            var change = SelectedChange;
            if (change == null || change.ColumnChanges == null || string.IsNullOrEmpty(newColumnName))
                return;

            var renamed = change.ColumnChanges.FirstOrDefault(c =>
                c.Status == ChangeStatus.Renamed
                && string.Equals(c.ColumnName, newColumnName, StringComparison.OrdinalIgnoreCase));
            if (renamed == null) return;

            // Reconstruct the original ADD and DROP
            var added = new ColumnChange
            {
                Id = renamed.Id,
                ColumnName = renamed.ColumnName,
                Status = ChangeStatus.New,
                SideA = renamed.SideA,
                SideB = null,
                Risk = RiskTier.Safe,
                Reasons = Array.Empty<RiskReason>(),
            };
            var dropped = new ColumnChange
            {
                Id = renamed.Id,
                ColumnName = renamed.OldColumnName ?? string.Empty,
                Status = ChangeStatus.Dropped,
                SideA = null,
                SideB = renamed.SideB,
                Risk = RiskTier.Destructive,
                Reasons = Array.Empty<RiskReason>(),
            };

            change.ColumnChanges.Remove(renamed);
            change.ColumnChanges.Add(added);
            change.ColumnChanges.Add(dropped);

            RecomputeChangeRisk(change);
            UpdateTableTree();
            OnPropertyChanged(nameof(SummaryText));
        }

        /// <summary>
        /// After mutating a Change's ColumnChanges (apply/undo rename), recompute
        /// the top-level Change.Risk and refresh the tree leaf so the icon, risk
        /// label, and summary counts all reflect the new state. Also triggers
        /// the destructive-gauntlet decision correctly in Apply Live.
        /// </summary>
        private void RecomputeChangeRisk(Change change)
        {
            // Re-classify column-level risks first (for the new/changed entries)
            foreach (var col in change.ColumnChanges)
            {
                var (colTier, colReasons) = ColumnRiskClassifier.Classify(col);
                col.Risk = colTier;
                col.Reasons = colReasons;
            }

            // Then aggregate to the top-level change
            var (tier, reasons) = RiskClassifier.Classify(change);
            change.Risk = tier;
            change.Reasons = reasons;

            // Refresh the tree leaf for this change so its icon / risk label
            // reflect the new risk tier.
            foreach (var group in TreeItems)
            {
                if (group.Children == null) continue;
                foreach (var leaf in group.Children)
                {
                    if (ReferenceEquals(leaf.Change, change))
                    {
                        bool reversed = Direction.Direction == SyncDirection.BtoA;
                        leaf.UpdateForDirection(reversed);
                        return;
                    }
                }
            }
        }

        public ICommand ApplyRenameCommand { get; }
        public ICommand UndoRenameCommand { get; }

        public ICommand CheckAllCommand { get; }
        public ICommand UncheckAllCommand { get; }
        public ICommand UncheckDestructiveCommand { get; }
        public ICommand UncheckRiskyCommand { get; }
        public ICommand CheckOnlySafeCommand { get; }
        public ICommand ClearFilterCommand { get; }

        public string FilterText
        {
            get => _filterText;
            set
            {
                if (SetProperty(ref _filterText, value))
                    ApplyFilter();
            }
        }

        private void ApplyFilter()
        {
            var needle = (_filterText ?? string.Empty).Trim();
            bool hasFilter = needle.Length > 0;

            foreach (var group in TreeItems)
            {
                if (group.Children == null) continue;
                bool anyVisible = false;
                foreach (var leaf in group.Children)
                {
                    bool matches = !hasFilter
                        || leaf.DisplayName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                    leaf.IsVisible = matches;
                    if (matches) anyVisible = true;
                }
                group.IsVisible = anyVisible;
                // Auto-expand groups when filtering, collapse when clearing
                if (hasFilter && anyVisible) group.IsExpanded = true;
                group.RefreshGroupCheckState();
            }
        }

        private IEnumerable<ChangeTreeItemViewModel> AllLeaves()
        {
            foreach (var group in TreeItems)
            {
                if (group.Children == null) continue;
                foreach (var leaf in group.Children)
                    yield return leaf;
            }
        }

        private void SetAllChecked(bool value)
        {
            foreach (var leaf in AllLeaves())
            {
                if (leaf.IsVisible)
                    leaf.IsChecked = value;
            }
            RefreshAllGroups();
            OnPropertyChanged(nameof(SummaryText));
        }

        private void SetCheckedByRisk(RiskTier risk, bool value)
        {
            foreach (var leaf in AllLeaves())
            {
                if (leaf.IsVisible && VisualRisk(leaf) == risk)
                    leaf.IsChecked = value;
            }
            RefreshAllGroups();
            OnPropertyChanged(nameof(SummaryText));
        }

        private RiskTier VisualRisk(ChangeTreeItemViewModel leaf)
        {
            if (leaf.Change == null) return RiskTier.Safe;
            bool reversed = Direction.Direction == SyncDirection.BtoA;
            var vs = leaf.Change.Status;
            if (reversed)
            {
                if (vs == ChangeStatus.New) vs = ChangeStatus.Dropped;
                else if (vs == ChangeStatus.Dropped) vs = ChangeStatus.New;
            }
            return ChangeTreeItemViewModel.GetDirectionalRisk(leaf.Change, vs);
        }

        private void RefreshAllGroups()
        {
            foreach (var group in TreeItems)
                group.RefreshGroupCheckState();
        }

        public SyncDirectionViewModel Direction { get; }
        public ObservableCollection<ChangeTreeItemViewModel> TreeItems { get; }
        public ComparisonResult ComparisonResult => _comparisonResult;

        public string ToastMessage
        {
            get => _toastMessage;
            set => SetProperty(ref _toastMessage, value);
        }

        public ObservableCollection<TableTreeNode> TableTreeItems
        {
            get => _tableTreeItems;
            set => SetProperty(ref _tableTreeItems, value);
        }

        public bool IsTableSelected
        {
            get => _isTableSelected;
            set => SetProperty(ref _isTableSelected, value);
        }

        public bool IsLoadingDdl
        {
            get => _isLoadingDdl;
            set => SetProperty(ref _isLoadingDdl, value);
        }

        public ChangeTreeItemViewModel SelectedTreeItem
        {
            get => _selectedTreeItem;
            set
            {
                if (SetProperty(ref _selectedTreeItem, value))
                {
                    OnPropertyChanged(nameof(SelectedChange));
                    OnPropertyChanged(nameof(SelectedObjectName));
                    OnPropertyChanged(nameof(SelectedRiskText));
                    OnPropertyChanged(nameof(SelectedSideBFileName));
                    UpdateTableTree();
                }
            }
        }

        public Change SelectedChange => _selectedTreeItem?.Change;

        /// <summary>
        /// Filename (no path) of the .sql file backing the selected change on
        /// Side B in folder mode. Empty string when Side B is a live database
        /// or when the object is New on Side A (no file yet). The Side B
        /// header in the results view appends this so the user always knows
        /// which file they're looking at.
        /// </summary>
        public string SelectedSideBFileName
        {
            get
            {
                var path = SelectedChange?.SourceFilePath;
                return string.IsNullOrEmpty(path) ? string.Empty : System.IO.Path.GetFileName(path);
            }
        }

        public string SelectedDdlA
        {
            get
            {
                var change = SelectedChange;
                return change?.DdlSideA ?? string.Empty;
            }
        }

        public string SelectedDdlB
        {
            get
            {
                var change = SelectedChange;
                return change?.DdlSideB ?? string.Empty;
            }
        }

        /// <summary>
        /// Loads table DDL for both sides on a background thread.
        /// Uses a generation counter to discard stale results when the user
        /// clicks a different item before loading completes.
        /// </summary>
        public async Task LoadDdlAsync()
        {
            var change = SelectedChange;
            if (change == null) return;

            bool needA = change.ObjectType == ObjectType.Table
                && string.IsNullOrEmpty(change.DdlSideA)
                && change.Status != ChangeStatus.Dropped
                && !string.IsNullOrEmpty(_connStrA);

            bool needB = change.ObjectType == ObjectType.Table
                && string.IsNullOrEmpty(change.DdlSideB)
                && change.Status != ChangeStatus.New
                && !string.IsNullOrEmpty(_connStrB);

            if (!needA && !needB) return;

            int generation = ++_ddlLoadGeneration;
            IsLoadingDdl = true;

            try
            {
                // Capture locals for the background work
                var connA = _connStrA;
                var connB = _connStrB;
                var dbA = _dbNameA;
                var dbB = _dbNameB;
                var schema = change.Id.Schema;
                var name = change.Id.Name;

                string ddlA = null;
                string ddlB = null;

                await Task.Run(() =>
                {
                    if (needA)
                    {
                        try
                        {
                            var reader = new SQLParity.Core.SchemaReader(connA, dbA);
                            ddlA = reader.ScriptTable(schema, name);
                        }
                        catch { }
                    }

                    if (needB)
                    {
                        try
                        {
                            var reader = new SQLParity.Core.SchemaReader(connB, dbB);
                            ddlB = reader.ScriptTable(schema, name);
                        }
                        catch { }
                    }
                });

                // Discard if the user selected a different item while we were loading
                if (generation != _ddlLoadGeneration) return;

                if (ddlA != null) change.DdlSideA = ddlA;
                if (ddlB != null) change.DdlSideB = ddlB;

                OnPropertyChanged(nameof(SelectedDdlA));
                OnPropertyChanged(nameof(SelectedDdlB));
            }
            finally
            {
                if (generation == _ddlLoadGeneration)
                    IsLoadingDdl = false;
            }
        }

        public string SelectedObjectName =>
            SelectedChange != null ? SelectedChange.Id.ToString() : string.Empty;

        public string SelectedRiskText =>
            SelectedChange != null
                ? SelectedChange.Risk.ToString()
                : string.Empty;

        public string SummaryText
        {
            get
            {
                if (_comparisonResult == null) return string.Empty;

                bool reversed = Direction.Direction == SyncDirection.BtoA;
                int safe = 0, caution = 0, risky = 0, destructive = 0;
                foreach (var change in _comparisonResult.Changes)
                {
                    var visualStatus = change.Status;
                    if (reversed)
                    {
                        if (change.Status == ChangeStatus.New) visualStatus = ChangeStatus.Dropped;
                        else if (change.Status == ChangeStatus.Dropped) visualStatus = ChangeStatus.New;
                    }
                    var risk = ChangeTreeItemViewModel.GetDirectionalRisk(change, visualStatus);
                    switch (risk)
                    {
                        case RiskTier.Safe: safe++; break;
                        case RiskTier.Caution: caution++; break;
                        case RiskTier.Risky: risky++; break;
                        case RiskTier.Destructive: destructive++; break;
                    }
                }

                int selected = GetSelectedChanges().Count();
                return string.Format(
                    "{0} differences \u2014 {1} safe, {2} caution, {3} risky, {4} destructive. {5} selected for sync.",
                    _comparisonResult.TotalCount,
                    safe, caution, risky, destructive,
                    selected);
            }
        }

        // Connection info stored for lazy DDL scripting
        private string _connStrA;
        private string _connStrB;
        private string _dbNameA;
        private string _dbNameB;

        public void Populate(ComparisonResult result, ConnectionSideViewModel sideA, ConnectionSideViewModel sideB)
        {
            _comparisonResult = result;
            _connStrA = sideA.BuildConnectionString();
            _connStrB = sideB.BuildConnectionString();
            _dbNameA = sideA.DatabaseName;
            _dbNameB = sideB.DatabaseName;
            Direction.PopulateFrom(sideA, sideB);
            Direction.Direction = SyncDirection.AtoB; // Default to A→B

            // Build tree in a temporary list to avoid per-item UI updates
            var tempGroups = new List<ChangeTreeItemViewModel>();

            // Group by (ObjectType, SourceDatabase) so multi-DB folder-mode
            // comparisons split each object-type branch into one node per
            // database. SourceDatabase is null for single-DB compares (live
            // vs live), in which case the label is just ObjectType.ToString()
            // and behaviour matches the pre-multi-DB tree.
            var grouped = result.Changes
                .GroupBy(c => new { c.ObjectType, c.SourceDatabase })
                .OrderBy(g => g.Key.ObjectType.ToString(), StringComparer.Ordinal)
                .ThenBy(g => g.Key.SourceDatabase ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            foreach (var group in grouped)
            {
                string groupLabel = string.IsNullOrEmpty(group.Key.SourceDatabase)
                    ? group.Key.ObjectType.ToString()
                    : $"{group.Key.ObjectType} ({group.Key.SourceDatabase})";

                var groupNode = new ChangeTreeItemViewModel(
                    groupLabel,
                    isGroup: true,
                    change: null);

                foreach (var change in group.OrderBy(c => c.Id.ToString()))
                {
                    var leaf = new ChangeTreeItemViewModel(
                        change.Id.ToString(),
                        isGroup: false,
                        change: change);
                    leaf.PropertyChanged += OnLeafPropertyChanged;

                    groupNode.Children.Add(leaf);
                }

                tempGroups.Add(groupNode);
            }

            // Only auto-expand groups if the total change count is manageable
            // (expanding thousands of items freezes the WPF TreeView)
            int totalChanges = result.TotalCount;
            bool autoExpand = totalChanges <= 100;
            foreach (var g in tempGroups)
                g.IsExpanded = autoExpand;

            // Replace the collection in one shot — single UI update
            TreeItems.Clear();
            foreach (var g in tempGroups)
                TreeItems.Add(g);

            OnPropertyChanged(nameof(SummaryText));
        }

        public IEnumerable<Change> GetSelectedChanges()
        {
            foreach (var group in TreeItems)
            {
                if (group.Children == null) continue;
                foreach (var leaf in group.Children)
                {
                    if (leaf.IsChecked == true && leaf.Change != null)
                        yield return leaf.Change;
                }
            }
        }

        public void RefreshSummary()
        {
            OnPropertyChanged(nameof(SummaryText));
        }

        private void UpdateTableTree()
        {
            var change = SelectedChange;
            if (change != null && change.ObjectType == ObjectType.Table && _comparisonResult != null)
            {
                var tableId = change.Id;
                TableModel tableA = _comparisonResult.SideA.Tables.FirstOrDefault(t => t.Id.Equals(tableId));
                TableModel tableB = _comparisonResult.SideB.Tables.FirstOrDefault(t => t.Id.Equals(tableId));

                var columnChanges = change.ColumnChanges ?? (IList<ColumnChange>)new ColumnChange[0];
                bool reverseDirection = Direction.Direction == SyncDirection.BtoA;
                TableTreeItems = TableDiffTreeBuilder.Build(tableA, tableB, columnChanges, reverseDirection,
                    ApplyRenameCommand, UndoRenameCommand);
                IsTableSelected = true;
            }
            else
            {
                TableTreeItems = null;
                IsTableSelected = false;
            }
        }

        private void UpdateTreeForDirection()
        {
            bool reversed = Direction.Direction == SyncDirection.BtoA;
            foreach (var group in TreeItems)
            {
                if (group.Children == null) continue;
                foreach (var leaf in group.Children)
                {
                    leaf.UpdateForDirection(reversed);
                }
            }
        }

        private void OnLeafPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChangeTreeItemViewModel.IsChecked))
            {
                OnPropertyChanged(nameof(SummaryText));
                // Refresh the parent group's tri-state
                if (sender is ChangeTreeItemViewModel leaf && !leaf.IsGroup)
                {
                    foreach (var group in TreeItems)
                    {
                        if (group.Children != null && group.Children.Contains(leaf))
                        {
                            group.RefreshGroupCheckState();
                            break;
                        }
                    }
                }
            }
        }

        private void OnDirectionChanged(object sender, EventArgs e)
        {
            if (_comparisonResult == null)
                return;

            // Update tree icons for new direction
            UpdateTreeForDirection();

            // Refresh summary counts for the new direction
            OnPropertyChanged(nameof(SummaryText));

            // Count destructive changes in the new direction for the toast
            bool reversed = Direction.Direction == SyncDirection.BtoA;
            int destructive = 0;
            foreach (var change in _comparisonResult.Changes)
            {
                var vs = change.Status;
                if (reversed)
                {
                    if (change.Status == ChangeStatus.New) vs = ChangeStatus.Dropped;
                    else if (change.Status == ChangeStatus.Dropped) vs = ChangeStatus.New;
                }
                if (ChangeTreeItemViewModel.GetDirectionalRisk(change, vs) == RiskTier.Destructive)
                    destructive++;
            }
            ToastMessage = string.Format("Direction flipped. {0} changes are destructive.", destructive);

            if (_toastTimer != null)
            {
                _toastTimer.Stop();
            }

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _toastTimer.Tick += (s, args) =>
            {
                ToastMessage = string.Empty;
                _toastTimer.Stop();
            };
            _toastTimer.Start();

            // Rebuild the table tree if a table is selected — direction affects add/drop display
            UpdateTableTree();
        }
    }
}
