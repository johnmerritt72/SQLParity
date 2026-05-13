using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using SQLParity.Core.Model;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class ChangeTreeItemViewModel : ViewModelBase
    {
        private bool _isSelected;
        private bool? _isChecked = true;
        private bool _isExpanded = true;
        private bool _isVisible = true;
        private bool _suppressChildUpdate;
        private string _statusIcon = string.Empty;
        private string _statusLabel = string.Empty;

        public ChangeTreeItemViewModel(string displayName, bool isGroup, Change change)
        {
            DisplayName = displayName;
            IsGroup = isGroup;
            Change = change;

            if (isGroup)
            {
                Children = new ObservableCollection<ChangeTreeItemViewModel>();
            }

            if (change != null)
            {
                UpdateForDirection(false);
            }
        }

        public string DisplayName { get; }
        public bool IsGroup { get; }
        public Change Change { get; }
        public ObservableCollection<ChangeTreeItemViewModel> Children { get; }

        /// <summary>True if this change references external databases or linked servers.</summary>
        public bool HasExternalReferences =>
            Change != null && Change.ExternalReferences != null && Change.ExternalReferences.Count > 0;

        /// <summary>Tooltip text describing the external references.</summary>
        public string ExternalReferencesTooltip
        {
            get
            {
                if (!HasExternalReferences) return string.Empty;
                return "References external databases / linked servers that may not exist on the destination:\n\n• "
                    + string.Join("\n• ", Change.ExternalReferences);
            }
        }

        /// <summary>
        /// Names of orphan-counterpart candidates this leaf could be paired with
        /// (filename matches a DB-orphan's name, or vice versa). Empty when there's
        /// no candidate.
        /// </summary>
        public IReadOnlyList<string> RenameCandidateNames =>
            Change?.RenameCandidateNames ?? (IReadOnlyList<string>)System.Array.Empty<string>();

        public bool HasRenameCandidates => RenameCandidateNames.Count > 0;

        /// <summary>First candidate name (the one shown in the hint button).</summary>
        public string FirstRenameCandidate =>
            HasRenameCandidates ? RenameCandidateNames[0] : string.Empty;

        /// <summary>Hint button text, e.g. "↻ pair with [Foo]?".</summary>
        public string PairWithHint =>
            HasRenameCandidates ? "↻ pair with [" + FirstRenameCandidate + "]?" : string.Empty;

        /// <summary>True when this leaf is the result of a typo-rename pairing.</summary>
        public bool IsPaired =>
            Change != null && !string.IsNullOrEmpty(Change.PairedFromName);

        /// <summary>Command set by ResultsViewModel — invokes pair-as-typo-rename.</summary>
        public ICommand PairAsTypoRenameCommand { get; set; }

        /// <summary>Command set by ResultsViewModel — splits a paired change back into DROP + NEW.</summary>
        public ICommand UndoPairCommand { get; set; }

        public string StatusIcon
        {
            get => _statusIcon;
            set => SetProperty(ref _statusIcon, value);
        }

        public string StatusLabel
        {
            get => _statusLabel;
            set => SetProperty(ref _statusLabel, value);
        }

        // RiskIcon doesn't change with direction
        public string RiskIcon { get; private set; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool? IsChecked
        {
            get => _isChecked;
            set
            {
                if (SetProperty(ref _isChecked, value))
                {
                    // Group checkbox: propagate to children
                    if (IsGroup && Children != null && !_suppressChildUpdate && value.HasValue)
                    {
                        foreach (var child in Children)
                        {
                            if (child.IsVisible)
                                child.IsChecked = value.Value;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Updates the group's IsChecked state based on its children.
        /// All checked → true, all unchecked → false, mixed → null (indeterminate).
        /// Only affects visible children (so filter doesn't mislead the state).
        /// </summary>
        public void RefreshGroupCheckState()
        {
            if (!IsGroup || Children == null) return;

            bool anyChecked = false;
            bool anyUnchecked = false;
            foreach (var child in Children)
            {
                if (!child.IsVisible) continue;
                if (child.IsChecked == true) anyChecked = true;
                else anyUnchecked = true;
                if (anyChecked && anyUnchecked) break;
            }

            bool? newState;
            if (anyChecked && !anyUnchecked) newState = true;
            else if (!anyChecked && anyUnchecked) newState = false;
            else if (anyChecked && anyUnchecked) newState = null;
            else newState = _isChecked; // no visible children — keep current

            _suppressChildUpdate = true;
            try { IsChecked = newState; }
            finally { _suppressChildUpdate = false; }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        /// <summary>
        /// Updates the status icon/label based on sync direction.
        /// When reversed, New becomes DROP and Dropped becomes ADD.
        /// </summary>
        public void UpdateForDirection(bool reversed)
        {
            if (Change == null) return;

            var visualStatus = Change.Status;
            if (reversed)
            {
                if (Change.Status == ChangeStatus.New) visualStatus = ChangeStatus.Dropped;
                else if (Change.Status == ChangeStatus.Dropped) visualStatus = ChangeStatus.New;
            }

            StatusIcon = GetStatusIcon(visualStatus, reversed);
            StatusLabel = GetStatusLabel(visualStatus);
            RiskIcon = GetRiskIcon(GetDirectionalRisk(Change, visualStatus));
            OnPropertyChanged(nameof(RiskIcon));
        }

        /// <summary>
        /// Returns the risk for the visual status. When direction is flipped,
        /// an ADD (Safe) becomes a DROP (Destructive) and vice versa.
        /// Modified items keep their original risk.
        /// </summary>
        internal static RiskTier GetDirectionalRisk(Change change, ChangeStatus visualStatus)
        {
            if (visualStatus == change.Status)
                return change.Risk;

            // Direction was flipped — recompute based on visual status
            if (visualStatus == ChangeStatus.New)
                return RiskTier.Safe;
            if (visualStatus == ChangeStatus.Dropped)
                return RiskTier.Destructive;

            return change.Risk;
        }

        /// <summary>
        /// Returns the status icon for a tree item.
        /// <paramref name="status"/> is the visual status (already swapped for direction).
        /// <paramref name="reversed"/> controls the arrow direction on ADD/DROP items.
        /// </summary>
        public static string GetStatusIcon(ChangeStatus status, bool reversed = false)
        {
            switch (status)
            {
                case ChangeStatus.New: return reversed ? "\u2190" : "\u2192";  // ← or → for ADD
                case ChangeStatus.Modified: return "\u270E";                   // ✎ pencil
                case ChangeStatus.Dropped: return reversed ? "\u2190" : "\u2192";  // ← or → for DROP
                default: return "?";
            }
        }

        public static string GetStatusLabel(ChangeStatus status)
        {
            switch (status)
            {
                case ChangeStatus.New: return "ADD";
                case ChangeStatus.Modified: return "MODIFY";
                case ChangeStatus.Dropped: return "DROP";
                default: return "";
            }
        }

        public static string GetRiskIcon(RiskTier risk)
        {
            switch (risk)
            {
                case RiskTier.Safe: return "SAFE";
                case RiskTier.Caution: return "CAUTION";
                case RiskTier.Risky: return "RISKY";
                case RiskTier.Destructive: return "DESTRUCTIVE";
                default: return "";
            }
        }
    }
}
