using System;
using System.Windows.Input;
using SQLParity.Core.Model;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public enum SyncDirection
    {
        Unset,
        AtoB,
        BtoA
    }

    public class SyncDirectionViewModel : ViewModelBase
    {
        private SyncDirection _direction = SyncDirection.Unset;
        private string _labelA = string.Empty;
        private string _labelB = string.Empty;
        private string _labelAWithDb = string.Empty;
        private string _labelBWithDb = string.Empty;
        private EnvironmentTag _tagA;
        private EnvironmentTag _tagB;

        public SyncDirectionViewModel()
        {
            SetAtoBCommand = new RelayCommand(_ => Direction = SyncDirection.AtoB);
            SetBtoACommand = new RelayCommand(_ => Direction = SyncDirection.BtoA);
            FlipCommand = new RelayCommand(_ =>
            {
                if (Direction == SyncDirection.AtoB)
                    Direction = SyncDirection.BtoA;
                else if (Direction == SyncDirection.BtoA)
                    Direction = SyncDirection.AtoB;
            }, _ => Direction != SyncDirection.Unset);
        }

        public SyncDirection Direction
        {
            get => _direction;
            set
            {
                if (SetProperty(ref _direction, value))
                {
                    OnPropertyChanged(nameof(ArrowText));
                    OnPropertyChanged(nameof(DestinationLabel));
                    OnPropertyChanged(nameof(DestinationTag));
                    OnPropertyChanged(nameof(IsDestinationProd));
                    OnPropertyChanged(nameof(SourceLabel));
                    OnPropertyChanged(nameof(SourceTag));
                    DirectionChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string LabelA
        {
            get => _labelA;
            set
            {
                if (SetProperty(ref _labelA, value))
                    OnPropertyChanged(nameof(ArrowText));
            }
        }

        public string LabelB
        {
            get => _labelB;
            set
            {
                if (SetProperty(ref _labelB, value))
                    OnPropertyChanged(nameof(ArrowText));
            }
        }

        public EnvironmentTag TagA
        {
            get => _tagA;
            set => SetProperty(ref _tagA, value);
        }

        public EnvironmentTag TagB
        {
            get => _tagB;
            set => SetProperty(ref _tagB, value);
        }

        public string ArrowText
        {
            get
            {
                switch (Direction)
                {
                    case SyncDirection.AtoB:
                        return LabelA + "  \u2500\u2500\u25B6  " + LabelB;
                    case SyncDirection.BtoA:
                        return LabelA + "  \u25C0\u2500\u2500  " + LabelB;
                    default:
                        return "Select sync direction";
                }
            }
        }

        public string DestinationLabel
        {
            get
            {
                switch (Direction)
                {
                    case SyncDirection.AtoB: return LabelB;
                    case SyncDirection.BtoA: return LabelA;
                    default: return string.Empty;
                }
            }
        }

        public EnvironmentTag DestinationTag
        {
            get
            {
                switch (Direction)
                {
                    case SyncDirection.AtoB: return TagB;
                    case SyncDirection.BtoA: return TagA;
                    default: return EnvironmentTag.Untagged;
                }
            }
        }

        public string SourceLabel
        {
            get
            {
                switch (Direction)
                {
                    case SyncDirection.AtoB: return LabelA;
                    case SyncDirection.BtoA: return LabelB;
                    default: return string.Empty;
                }
            }
        }

        public EnvironmentTag SourceTag
        {
            get
            {
                switch (Direction)
                {
                    case SyncDirection.AtoB: return TagA;
                    case SyncDirection.BtoA: return TagB;
                    default: return EnvironmentTag.Untagged;
                }
            }
        }

        public bool IsDestinationProd => DestinationTag == EnvironmentTag.Prod;

        public ICommand SetAtoBCommand { get; }
        public ICommand SetBtoACommand { get; }
        public ICommand FlipCommand { get; }

        public event EventHandler DirectionChanged;

        /// <summary>
        /// Label + " (DatabaseName)" suffix for Side A's results-pane header.
        /// Falls back to <see cref="LabelA"/> when the side is in folder mode
        /// or has no database name. Updated by <see cref="PopulateFrom"/>;
        /// not driven by the <see cref="LabelA"/> setter because the suffix
        /// depends on the side's database/folder state, not on the label
        /// alone.
        /// </summary>
        public string LabelAWithDb
        {
            get => _labelAWithDb;
            private set => SetProperty(ref _labelAWithDb, value);
        }

        /// <summary>
        /// Label + " (DatabaseName)" suffix for Side B's results-pane header.
        /// Falls back to <see cref="LabelB"/> when the side is in folder mode
        /// or has no database name.
        /// </summary>
        public string LabelBWithDb
        {
            get => _labelBWithDb;
            private set => SetProperty(ref _labelBWithDb, value);
        }

        public void PopulateFrom(ConnectionSideViewModel sideA, ConnectionSideViewModel sideB)
        {
            LabelA = sideA.Label;
            TagA = sideA.Tag;
            LabelB = sideB.Label;
            TagB = sideB.Tag;
            LabelAWithDb = ComposeLabelWithDb(sideA);
            LabelBWithDb = ComposeLabelWithDb(sideB);
            Direction = SyncDirection.Unset;
        }

        private static string ComposeLabelWithDb(ConnectionSideViewModel side)
        {
            if (side.IsFolderMode || string.IsNullOrWhiteSpace(side.DatabaseName))
                return side.Label;
            return $"{side.Label} ({side.DatabaseName})";
        }
    }
}
