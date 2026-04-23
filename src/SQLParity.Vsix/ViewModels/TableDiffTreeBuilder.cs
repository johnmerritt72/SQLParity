using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SQLParity.Core.Model;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    /// <summary>
    /// A node in the table detail tree (column, index, FK, etc.).
    /// </summary>
    public class TableTreeNode : ViewModelBase
    {
        public string DisplayName { get; set; }
        public string StatusIcon { get; set; } = string.Empty;
        public string RiskIcon { get; set; } = string.Empty;
        public bool IsGroup { get; set; }
        public bool IsStrikethrough { get; set; }
        public ChangeStatus? Status { get; set; }
        public RiskTier? Risk { get; set; }
        public ObservableCollection<TableTreeNode> Children { get; set; }

        /// <summary>
        /// For a NEW column that has candidate dropped columns matching its type:
        /// the list of old names the user could map this as a rename from.
        /// Null if this isn't a rename candidate.
        /// </summary>
        public List<string> RenameCandidateOldNames { get; set; }

        /// <summary>True if <see cref="RenameCandidateOldNames"/> is non-empty.</summary>
        public bool HasRenameCandidates => RenameCandidateOldNames != null && RenameCandidateOldNames.Count > 0;

        /// <summary>First candidate old name, or null.</summary>
        public string FirstRenameCandidate => HasRenameCandidates ? RenameCandidateOldNames[0] : null;

        /// <summary>Hint text for the rename button.</summary>
        public string RenameFromHint => HasRenameCandidates ? $"↻ rename from {FirstRenameCandidate}" : string.Empty;

        /// <summary>For a RENAMED column: the old name it was mapped from.</summary>
        public string RenameOldName { get; set; }

        public bool IsRename => !string.IsNullOrEmpty(RenameOldName);

        /// <summary>Command to apply a rename. Parameter is the old column name (string).</summary>
        public ICommand ApplyRenameCommand { get; set; }

        /// <summary>Command to undo a rename. Parameter is the new column name (string).</summary>
        public ICommand UndoRenameCommand { get; set; }

        /// <summary>The current column name (for command parameter resolution).</summary>
        public string ColumnName { get; set; }

        public TableTreeNode()
        {
            Children = new ObservableCollection<TableTreeNode>();
        }
    }

    /// <summary>
    /// Builds a tree of <see cref="TableTreeNode"/> items showing the detailed
    /// diff between two versions of a table.
    /// </summary>
    public static class TableDiffTreeBuilder
    {
        /// <summary>
        /// Builds the table diff tree. When <paramref name="reverseDirection"/> is true,
        /// the visual meaning of New and Dropped is swapped (because syncing B→A means
        /// a column only in A would be dropped from A, not added to B).
        /// </summary>
        public static ObservableCollection<TableTreeNode> Build(
            TableModel tableA,
            TableModel tableB,
            IList<ColumnChange> columnChanges,
            bool reverseDirection = false,
            ICommand applyRenameCommand = null,
            ICommand undoRenameCommand = null)
        {
            var root = new ObservableCollection<TableTreeNode>();

            root.Add(BuildColumnsGroup(tableA, tableB, columnChanges, reverseDirection, applyRenameCommand, undoRenameCommand));
            root.Add(BuildNameMatchGroup("Indexes",
                tableA?.Indexes, tableB?.Indexes,
                i => i.Name, FormatIndex, reverseDirection));
            root.Add(BuildNameMatchGroup("Foreign Keys",
                tableA?.ForeignKeys, tableB?.ForeignKeys,
                fk => fk.Name, FormatForeignKey, reverseDirection));
            root.Add(BuildNameMatchGroup("Check Constraints",
                tableA?.CheckConstraints, tableB?.CheckConstraints,
                ck => ck.Name, FormatCheckConstraint, reverseDirection));
            root.Add(BuildNameMatchGroup("Triggers",
                tableA?.Triggers, tableB?.Triggers,
                t => t.Name, FormatTrigger, reverseDirection));

            return root;
        }

        private static TableTreeNode BuildColumnsGroup(
            TableModel tableA,
            TableModel tableB,
            IList<ColumnChange> columnChanges,
            bool reverseDirection,
            ICommand applyRenameCommand,
            ICommand undoRenameCommand)
        {
            var group = new TableTreeNode { DisplayName = "Columns", IsGroup = true };

            // Build a lookup of column changes by column name
            var changesByName = columnChanges != null
                ? columnChanges.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ColumnChange>(StringComparer.OrdinalIgnoreCase);

            // Detect rename candidates: NEW columns whose type matches a DROPPED column.
            // Keyed by NEW column name, value is list of candidate old names.
            var renameCandidates = DetectRenameCandidates(columnChanges);

            // Track which dropped columns are already accounted for by a rename
            // so we don't render them twice.
            var renamedOldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (columnChanges != null)
            {
                foreach (var cc in columnChanges)
                {
                    if (cc.Status == ChangeStatus.Renamed && !string.IsNullOrEmpty(cc.OldColumnName))
                        renamedOldNames.Add(cc.OldColumnName);
                }
            }

            // Collect all column names preserving order: SideA columns first, then new SideB-only columns
            var allColumns = new List<ColumnModel>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (tableA != null)
            {
                foreach (var col in tableA.Columns)
                {
                    allColumns.Add(col);
                    seenNames.Add(col.Name);
                }
            }

            if (tableB != null)
            {
                foreach (var col in tableB.Columns)
                {
                    if (!seenNames.Contains(col.Name))
                        allColumns.Add(col);
                }
            }

            foreach (var col in allColumns)
            {
                // Skip dropped columns that have been mapped as a rename —
                // the rename shows up under the NEW column name.
                if (renamedOldNames.Contains(col.Name) && !changesByName.ContainsKey(col.Name))
                    continue;

                var node = new TableTreeNode { ColumnName = col.Name };

                if (changesByName.TryGetValue(col.Name, out var cc))
                {
                    if (cc.Status == ChangeStatus.Renamed)
                    {
                        node.Status = ChangeStatus.Renamed;
                        node.StatusIcon = "\u21BB"; // ↻ rotate arrow
                        node.RiskIcon = ChangeTreeItemViewModel.GetRiskIcon(cc.Risk);
                        node.Risk = cc.Risk;
                        node.RenameOldName = cc.OldColumnName;
                        node.UndoRenameCommand = undoRenameCommand;
                        node.DisplayName = $"{cc.OldColumnName} → {FormatColumn(cc.SideA ?? col)}";
                        group.Children.Add(node);
                        continue;
                    }

                    // Determine the visual status: when reversed, New becomes Dropped and vice versa
                    var visualStatus = cc.Status;
                    if (reverseDirection)
                    {
                        if (cc.Status == ChangeStatus.New) visualStatus = ChangeStatus.Dropped;
                        else if (cc.Status == ChangeStatus.Dropped) visualStatus = ChangeStatus.New;
                    }

                    node.Status = visualStatus;
                    node.StatusIcon = ChangeTreeItemViewModel.GetStatusIcon(visualStatus, reverseDirection);
                    node.RiskIcon = ChangeTreeItemViewModel.GetRiskIcon(cc.Risk);
                    node.Risk = cc.Risk;
                    node.IsStrikethrough = visualStatus == ChangeStatus.Dropped;

                    // If this is a NEW column and there are candidate drops, offer rename
                    if (cc.Status == ChangeStatus.New && renameCandidates.TryGetValue(col.Name, out var candidates))
                    {
                        node.RenameCandidateOldNames = candidates;
                        node.ApplyRenameCommand = applyRenameCommand;
                    }

                    switch (cc.Status)
                    {
                        case ChangeStatus.New:
                            node.DisplayName = FormatColumn(cc.SideA ?? col);
                            break;
                        case ChangeStatus.Dropped:
                            node.DisplayName = FormatColumn(cc.SideB ?? col);
                            break;
                        case ChangeStatus.Modified:
                            node.DisplayName = FormatColumnModified(cc.SideA, cc.SideB);
                            break;
                        default:
                            node.DisplayName = FormatColumn(col);
                            break;
                    }
                }
                else
                {
                    // Unchanged
                    node.DisplayName = FormatColumn(col);
                }

                group.Children.Add(node);
            }

            return group;
        }

        /// <summary>
        /// Finds likely rename candidates: pairs of NEW and DROPPED columns with
        /// identical data type, length, precision, scale, and nullability.
        /// Returns a map of new-column-name → list of candidate old names.
        /// </summary>
        private static Dictionary<string, List<string>> DetectRenameCandidates(IList<ColumnChange> columnChanges)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (columnChanges == null) return result;

            var newCols = columnChanges.Where(c => c.Status == ChangeStatus.New && c.SideA != null).ToList();
            var droppedCols = columnChanges.Where(c => c.Status == ChangeStatus.Dropped && c.SideB != null).ToList();

            foreach (var newCc in newCols)
            {
                var a = newCc.SideA;
                var matches = new List<string>();
                foreach (var dropCc in droppedCols)
                {
                    var b = dropCc.SideB;
                    if (string.Equals(a.DataType, b.DataType, StringComparison.OrdinalIgnoreCase)
                        && a.MaxLength == b.MaxLength
                        && a.Precision == b.Precision
                        && a.Scale == b.Scale
                        && a.IsNullable == b.IsNullable)
                    {
                        matches.Add(dropCc.ColumnName);
                    }
                }
                if (matches.Count > 0)
                    result[newCc.ColumnName] = matches;
            }
            return result;
        }

        private static string FormatColumn(ColumnModel col)
        {
            var parts = new List<string> { col.Name, col.DataType };
            parts.Add(col.IsNullable ? "NULL" : "NOT NULL");
            if (col.IsIdentity)
                parts.Add("IDENTITY");
            if (col.DefaultConstraint != null)
                parts.Add("DEFAULT " + col.DefaultConstraint.Definition);
            return string.Join("  ", parts);
        }

        private static string FormatColumnModified(ColumnModel a, ColumnModel b)
        {
            // Show the column name plus what changed
            var name = a?.Name ?? b?.Name ?? "?";
            var changes = new List<string>();

            if (a != null && b != null)
            {
                if (!string.Equals(a.DataType, b.DataType, StringComparison.OrdinalIgnoreCase))
                    changes.Add(string.Format("{0} \u2192 {1}", b.DataType, a.DataType));
                if (a.IsNullable != b.IsNullable)
                    changes.Add(a.IsNullable ? "NOT NULL \u2192 NULL" : "NULL \u2192 NOT NULL");
                if (a.IsIdentity != b.IsIdentity)
                    changes.Add(a.IsIdentity ? "added IDENTITY" : "removed IDENTITY");
                var defA = a.DefaultConstraint?.Definition;
                var defB = b.DefaultConstraint?.Definition;
                if (!string.Equals(defA, defB, StringComparison.OrdinalIgnoreCase))
                {
                    if (defB == null) changes.Add("DEFAULT " + defA);
                    else if (defA == null) changes.Add("removed DEFAULT");
                    else changes.Add(string.Format("DEFAULT {0} \u2192 {1}", defB, defA));
                }
            }

            if (changes.Count > 0)
                return string.Format("{0}  ({1})", name, string.Join(", ", changes));

            return name + "  (modified)";
        }

        private static string FormatIndex(IndexModel idx)
        {
            var traits = new List<string>();
            if (idx.IsClustered) traits.Add("clustered");
            if (idx.IsPrimaryKey) traits.Add("primary key");
            if (idx.IsUnique && !idx.IsPrimaryKey) traits.Add("unique");
            if (idx.IsUniqueConstraint) traits.Add("unique constraint");

            return traits.Count > 0
                ? string.Format("{0}  ({1})", idx.Name, string.Join(", ", traits))
                : idx.Name;
        }

        private static string FormatForeignKey(ForeignKeyModel fk)
        {
            return string.Format("{0}  \u2192 [{1}].[{2}]", fk.Name, fk.ReferencedTableSchema, fk.ReferencedTableName);
        }

        private static string FormatCheckConstraint(CheckConstraintModel ck)
        {
            return string.Format("{0}  {1}", ck.Name, ck.Definition);
        }

        private static string FormatTrigger(TriggerModel t)
        {
            var events = new List<string>();
            if (t.FiresOnInsert) events.Add("INSERT");
            if (t.FiresOnUpdate) events.Add("UPDATE");
            if (t.FiresOnDelete) events.Add("DELETE");
            return string.Format("{0}  {1}", t.Name, string.Join(", ", events));
        }

        private static TableTreeNode BuildNameMatchGroup<T>(
            string groupName,
            IReadOnlyList<T> itemsA,
            IReadOnlyList<T> itemsB,
            Func<T, string> nameSelector,
            Func<T, string> formatter,
            bool reverseDirection = false)
        {
            var group = new TableTreeNode { DisplayName = groupName, IsGroup = true };

            var dictA = itemsA != null
                ? itemsA.ToDictionary(nameSelector, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            var dictB = itemsB != null
                ? itemsB.ToDictionary(nameSelector, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

            var allNames = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (itemsA != null)
            {
                foreach (var item in itemsA)
                {
                    var n = nameSelector(item);
                    if (seen.Add(n)) allNames.Add(n);
                }
            }
            if (itemsB != null)
            {
                foreach (var item in itemsB)
                {
                    var n = nameSelector(item);
                    if (seen.Add(n)) allNames.Add(n);
                }
            }

            foreach (var name in allNames)
            {
                var inA = dictA.ContainsKey(name);
                var inB = dictB.ContainsKey(name);
                var node = new TableTreeNode();

                if (inA && !inB)
                {
                    // In A but not B: if syncing A→B this is "add to B"; if B→A this is "drop from A"
                    var visualStatus = reverseDirection ? ChangeStatus.Dropped : ChangeStatus.New;
                    node.Status = visualStatus;
                    node.StatusIcon = ChangeTreeItemViewModel.GetStatusIcon(visualStatus, reverseDirection);
                    node.IsStrikethrough = visualStatus == ChangeStatus.Dropped;
                    node.DisplayName = formatter(dictA[name]);
                }
                else if (!inA && inB)
                {
                    // In B but not A: if syncing A→B this is "drop from B"; if B→A this is "add to A"
                    var visualStatus = reverseDirection ? ChangeStatus.New : ChangeStatus.Dropped;
                    node.Status = visualStatus;
                    node.StatusIcon = ChangeTreeItemViewModel.GetStatusIcon(visualStatus, reverseDirection);
                    node.IsStrikethrough = visualStatus == ChangeStatus.Dropped;
                    node.DisplayName = formatter(dictB[name]);
                }
                else
                {
                    // Unchanged (in both)
                    node.DisplayName = formatter(dictA[name]);
                }

                group.Children.Add(node);
            }

            return group;
        }
    }
}
