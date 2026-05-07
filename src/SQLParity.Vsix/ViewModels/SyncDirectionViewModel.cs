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
        private string _labelBWithDb = string.Empty;
        private EnvironmentTag _tagA;
        private EnvironmentTag _tagB;
        private bool _isBtoADangerous;
        private string _btoADangerExplanation = string.Empty;

        public SyncDirectionViewModel()
        {
            SetAtoBCommand = new RelayCommand(_ => Direction = SyncDirection.AtoB);

            // SetBtoACommand is blocked entirely when Side B is empty — flipping
            // direction to B→A would queue a "drop everything on Side A" apply,
            // which has no legitimate user intent.
            SetBtoACommand = new RelayCommand(
                _ => Direction = SyncDirection.BtoA,
                _ => !IsBtoADangerous);

            // FlipCommand is allowed only when:
            //   - a direction is set (today's existing rule), and
            //   - the flip would not land on B→A while it's flagged dangerous.
            // (Flipping FROM B→A back TO A→B is always safe.)
            FlipCommand = new RelayCommand(_ =>
            {
                if (Direction == SyncDirection.AtoB)
                    Direction = SyncDirection.BtoA;
                else if (Direction == SyncDirection.BtoA)
                    Direction = SyncDirection.AtoB;
            }, _ => Direction != SyncDirection.Unset
                    && !(Direction == SyncDirection.AtoB && IsBtoADangerous));
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
            LabelBWithDb = ComposeLabelWithDb(sideB);
            Direction = SyncDirection.Unset;
        }

        /// <summary>
        /// True when applying the comparison in the B → A direction would drop
        /// every object on Side A — i.e. Side B's loaded schema is empty.
        /// While true, <see cref="SetBtoACommand"/> and the AtoB→BtoA branch of
        /// <see cref="FlipCommand"/> report CanExecute=false. Set by
        /// <see cref="ViewModels.ResultsViewModel.Populate"/> after each
        /// comparison.
        /// </summary>
        public bool IsBtoADangerous
        {
            get => _isBtoADangerous;
            set
            {
                if (SetProperty(ref _isBtoADangerous, value))
                    OnPropertyChanged(nameof(BtoAToolTip));
            }
        }

        /// <summary>
        /// Human-readable reason the B → A button is disabled. Used as the
        /// tooltip text for both the B→A button and the Flip button when
        /// <see cref="IsBtoADangerous"/> is true. Empty string when there is
        /// no current danger.
        /// </summary>
        public string BtoADangerExplanation
        {
            get => _btoADangerExplanation;
            set
            {
                if (SetProperty(ref _btoADangerExplanation, value))
                    OnPropertyChanged(nameof(BtoAToolTip));
            }
        }

        /// <summary>
        /// Tooltip exposed to the XAML for the B→A and Flip buttons.
        /// Returns the explanation string when B→A is dangerous, otherwise
        /// returns null so WPF skips rendering a tooltip entirely.
        /// </summary>
        public string BtoAToolTip => IsBtoADangerous ? _btoADangerExplanation : null;

        private static string ComposeLabelWithDb(ConnectionSideViewModel side)
        {
            if (side.IsFolderMode || string.IsNullOrWhiteSpace(side.DatabaseName))
                return side.Label;
            return $"{side.Label} ({side.DatabaseName})";
        }
    }
}
