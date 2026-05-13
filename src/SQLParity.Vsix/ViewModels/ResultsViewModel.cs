using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
        private string _filterText = string.Empty;
        private string _defaultDbNameA = string.Empty;

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
                    OnPropertyChanged(nameof(CurrentSideALabel));
                    UpdateTableTree();
                }
            }
        }

        public Change SelectedChange => _selectedTreeItem?.Change;

        /// <summary>
        /// Side A's pane-header label with a database-name suffix that tracks
        /// the currently-selected change. In multi-DB folder-mode comparisons
        /// the suffix matches <see cref="Change.SourceDatabase"/> of the
        /// selected change so the user can see which database the displayed
        /// object lives in. Falls back to a per-comparison default
        /// (<c>_defaultDbNameA</c>) before any change is selected — empty
        /// when the comparison spans 2+ source databases, the single source
        /// database when the folder targets one DB, or
        /// <c>SideA.DatabaseName</c> when the comparison is live-vs-live.
        /// </summary>
        public string CurrentSideALabel
        {
            get
            {
                var activeDbName = !string.IsNullOrEmpty(SelectedChange?.SourceDatabase)
                    ? SelectedChange.SourceDatabase
                    : _defaultDbNameA;

                return string.IsNullOrEmpty(activeDbName)
                    ? Direction.LabelA
                    : $"{Direction.LabelA} ({activeDbName})";
            }
        }

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

        public void Populate(ComparisonResult result, ConnectionSideViewModel sideA, ConnectionSideViewModel sideB)
        {
            _comparisonResult = result;

            // Determine the default DB-name suffix for Side A's header before
            // any change is selected. Three cases:
            //   - 0 distinct SourceDatabase values across changes → live-vs-live;
            //     fall back to the user's connection DatabaseName.
            //   - 1 distinct value → folder targets one DB; show that one (which
            //     may differ from what the user picked in Setup).
            //   - 2+ distinct values → folder spans multiple DBs; drop the
            //     suffix until the user clicks a change in the tree.
            var distinctSourceDbs = result.Changes
                .Select(c => c.SourceDatabase)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinctSourceDbs.Count == 0)
                _defaultDbNameA = sideA.DatabaseName ?? string.Empty;
            else if (distinctSourceDbs.Count == 1)
                _defaultDbNameA = distinctSourceDbs[0];
            else
                _defaultDbNameA = string.Empty;

            Direction.PopulateFrom(sideA, sideB);

            // Block B→A entirely when Side B has no objects — the comparison
            // would express every Side A object as a "drop on apply" and the
            // user has no legitimate reason to seed an empty DB by dropping
            // a populated one.
            Direction.IsBtoADangerous = !result.SideB.HasObjects;
            Direction.BtoADangerExplanation = Direction.IsBtoADangerous
                ? "Side B has no objects — applying B → A would drop everything on Side A. " +
                  "Pick a non-empty folder or database before reversing the direction."
                : string.Empty;

            Direction.Direction = SyncDirection.AtoB; // Default to A→B

            RebuildTree();

            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(CurrentSideALabel));
        }

        private void RebuildTree()
        {
            if (_comparisonResult == null)
            {
                TreeItems.Clear();
                return;
            }

            // Build tree in a temporary list to avoid per-item UI updates
            var tempGroups = new List<ChangeTreeItemViewModel>();

            // Group by (ObjectType, SourceDatabase) so multi-DB folder-mode
            // comparisons split each object-type branch into one node per
            // database. SourceDatabase is null for single-DB compares (live
            // vs live), in which case the label is just ObjectType.ToString()
            // and behaviour matches the pre-multi-DB tree.
            var grouped = _comparisonResult.Changes
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
                    // Wire pair commands (closures capture `leaf`)
                    var leafRef = leaf;
                    leaf.PairAsTypoRenameCommand = new RelayCommand(_ => PairAsTypoRename(leafRef));
                    leaf.UndoPairCommand = new RelayCommand(_ => UndoPair(leafRef));

                    groupNode.Children.Add(leaf);
                }

                tempGroups.Add(groupNode);
            }

            // Only auto-expand groups if the total change count is manageable
            // (expanding thousands of items freezes the WPF TreeView)
            int totalChanges = _comparisonResult.TotalCount;
            bool autoExpand = totalChanges <= 100;
            foreach (var g in tempGroups)
                g.IsExpanded = autoExpand;

            // Replace the collection in one shot — single UI update
            TreeItems.Clear();
            foreach (var g in tempGroups)
                TreeItems.Add(g);
        }

        private void PairAsTypoRename(ChangeTreeItemViewModel leaf)
        {
            if (leaf?.Change == null || !leaf.HasRenameCandidates || _comparisonResult == null)
                return;

            var thisChange = leaf.Change;
            var partnerName = leaf.FirstRenameCandidate;

            // Find the partner orphan: same Schema, name == partnerName, same SourceDatabase,
            // and the OPPOSITE Status (one NEW + one Dropped).
            SQLParity.Core.Model.Change partner = null;
            foreach (var c in _comparisonResult.Changes)
            {
                if (object.ReferenceEquals(c, thisChange)) continue;
                if (!string.Equals(c.Id.Schema, thisChange.Id.Schema, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(c.Id.Name, partnerName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(c.SourceDatabase, thisChange.SourceDatabase, StringComparison.OrdinalIgnoreCase)) continue;
                bool oppositeStatus =
                    (thisChange.Status == ChangeStatus.New && c.Status == ChangeStatus.Dropped)
                    || (thisChange.Status == ChangeStatus.Dropped && c.Status == ChangeStatus.New);
                if (!oppositeStatus) continue;
                partner = c;
                break;
            }
            if (partner == null) return;

            // Identify which is the DB-side (NEW: in DB but not in folder) and which is folder-side (DROP).
            var dbSide = thisChange.Status == ChangeStatus.New ? thisChange : partner;
            var folderSide = thisChange.Status == ChangeStatus.Dropped ? thisChange : partner;

            // The file's DDL becomes DdlSideA (the apply DDL — see Task 4 convention).
            // The DB's existing DDL becomes DdlSideB (display reference).
            // RewriteCreateNameIfPaired (in ScriptGenerator) will rewrite the CREATE
            // name token from PairedFromName (file's name) to Id.Name (DB's name) at apply time.
            var fileDdl = folderSide.DdlSideB ?? string.Empty;
            var dbDdl = dbSide.DdlSideA ?? string.Empty;

            // Always emit as Modified — the user sees the diff (identical bodies if
            // the only difference was the typo in the file's CREATE name) and can
            // uncheck the change if it's effectively a no-op.
            var combined = new SQLParity.Core.Model.Change
            {
                Id = dbSide.Id,                        // DB's correct name
                ObjectType = dbSide.ObjectType,
                Status = ChangeStatus.Modified,
                DdlSideA = fileDdl,                    // file's DDL (apply path)
                DdlSideB = dbDdl,                      // DB's DDL (display)
                PairedFromName = folderSide.Id.Name,   // file's CREATE name (for rewrite)
                ColumnChanges = Array.Empty<SQLParity.Core.Model.ColumnChange>(),
                SourceDatabase = dbSide.SourceDatabase,
                SourceFilePath = folderSide.SourceFilePath,
            };

            var changes = (IList<SQLParity.Core.Model.Change>)_comparisonResult.Changes;
            int idxThis = changes.IndexOf(thisChange);
            int idxPartner = changes.IndexOf(partner);
            int firstIdx = Math.Min(idxThis, idxPartner);
            changes.Remove(thisChange);
            changes.Remove(partner);
            changes.Insert(Math.Min(firstIdx, changes.Count), combined);

            RebuildTree();
            OnPropertyChanged(nameof(SummaryText));
        }

        private void UndoPair(ChangeTreeItemViewModel leaf)
        {
            if (leaf?.Change == null || string.IsNullOrEmpty(leaf.Change.PairedFromName) || _comparisonResult == null)
                return;
            var combined = leaf.Change;

            // Reconstruct the original NEW (DB-side, has DB's correct name) and DROP (folder-side, has typo name).
            var dbSide = new SQLParity.Core.Model.Change
            {
                Id = combined.Id,
                ObjectType = combined.ObjectType,
                Status = ChangeStatus.New,
                DdlSideA = combined.DdlSideB,   // DB's DDL was stored on SideB in the combined
                DdlSideB = null,
                ColumnChanges = Array.Empty<SQLParity.Core.Model.ColumnChange>(),
                SourceDatabase = combined.SourceDatabase,
            };
            var folderSide = new SQLParity.Core.Model.Change
            {
                Id = SchemaQualifiedName.TopLevel(combined.Id.Schema, combined.PairedFromName),
                ObjectType = combined.ObjectType,
                Status = ChangeStatus.Dropped,
                DdlSideA = null,
                DdlSideB = combined.DdlSideA,   // file's DDL was stored on SideA in the combined
                ColumnChanges = Array.Empty<SQLParity.Core.Model.ColumnChange>(),
                SourceDatabase = combined.SourceDatabase,
                SourceFilePath = combined.SourceFilePath,
            };
            // Re-attach the candidate hints so the user can re-pair if they want.
            dbSide.RenameCandidateNames.Add(combined.PairedFromName);
            folderSide.RenameCandidateNames.Add(combined.Id.Name);

            var changes = (IList<SQLParity.Core.Model.Change>)_comparisonResult.Changes;
            int idx = changes.IndexOf(combined);
            changes.Remove(combined);
            int insertAt = Math.Min(Math.Max(idx, 0), changes.Count);
            changes.Insert(insertAt, dbSide);
            changes.Insert(Math.Min(insertAt + 1, changes.Count), folderSide);

            RebuildTree();
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
