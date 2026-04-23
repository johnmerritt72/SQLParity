using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using SQLParity.Core.Model;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class GauntletViewModel : ViewModelBase
    {
        private string _destinationLabel = string.Empty;
        private EnvironmentTag _destinationTag;
        private SolidColorBrush _destinationColor;
        private string _typedLabel = string.Empty;
        private int _countdownSeconds = 3;
        private bool _timerExpired;
        private int _totalSelectedCount;
        private DispatcherTimer _timer;

        public string DestinationLabel
        {
            get => _destinationLabel;
            set => SetProperty(ref _destinationLabel, value);
        }

        public EnvironmentTag DestinationTag
        {
            get => _destinationTag;
            set => SetProperty(ref _destinationTag, value);
        }

        public SolidColorBrush DestinationColor
        {
            get => _destinationColor;
            set => SetProperty(ref _destinationColor, value);
        }

        public List<Change> DestructiveChanges { get; private set; } = new List<Change>();

        public int DestructiveCount => DestructiveChanges.Count;

        public int TotalSelectedCount
        {
            get => _totalSelectedCount;
            set => SetProperty(ref _totalSelectedCount, value);
        }

        public string TypedLabel
        {
            get => _typedLabel;
            set
            {
                if (SetProperty(ref _typedLabel, value))
                    OnPropertyChanged(nameof(LabelMatches));
            }
        }

        public bool LabelMatches => string.Equals(TypedLabel, DestinationLabel, StringComparison.Ordinal);

        public int CountdownSeconds
        {
            get => _countdownSeconds;
            set => SetProperty(ref _countdownSeconds, value);
        }

        public bool TimerExpired
        {
            get => _timerExpired;
            set
            {
                if (SetProperty(ref _timerExpired, value))
                    OnPropertyChanged(nameof(CanProceedFinal));
            }
        }

        public bool CanProceedFinal => TimerExpired;

        public void Populate(IEnumerable<Change> selectedChanges, string destinationLabel, EnvironmentTag destinationTag)
        {
            var allSelected = selectedChanges.ToList();
            TotalSelectedCount = allSelected.Count;
            DestructiveChanges = allSelected.Where(c => c.Risk == RiskTier.Destructive).ToList();
            OnPropertyChanged(nameof(DestructiveCount));

            DestinationLabel = destinationLabel;
            DestinationTag = destinationTag;
            DestinationColor = EnvironmentTagColors.GetBrush(destinationTag);

            TypedLabel = string.Empty;
            CountdownSeconds = 3;
            TimerExpired = false;
        }

        public void StartCountdown()
        {
            CountdownSeconds = 3;
            TimerExpired = false;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (s, e) =>
            {
                CountdownSeconds--;
                if (CountdownSeconds <= 0)
                {
                    _timer.Stop();
                    TimerExpired = true;
                }
            };
            _timer.Start();
        }
    }
}
