using System;
using System.Windows.Input;
using System.Windows.Media;
using SQLParity.Core.Model;
using SQLParity.Vsix.Helpers;

namespace SQLParity.Vsix.ViewModels
{
    public class ConfirmationViewModel : ViewModelBase
    {
        private string _labelA;
        private string _serverA;
        private string _databaseA;
        private EnvironmentTag _tagA;
        private SolidColorBrush _colorA;

        private string _labelB;
        private string _serverB;
        private string _databaseB;
        private EnvironmentTag _tagB;
        private SolidColorBrush _colorB;

        public ConfirmationViewModel()
        {
            BeginComparisonCommand = new RelayCommand(_ => BeginComparisonRequested?.Invoke(this, EventArgs.Empty));
            BackCommand = new RelayCommand(_ => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        public string LabelA { get => _labelA; set => SetProperty(ref _labelA, value); }
        public string ServerA { get => _serverA; set => SetProperty(ref _serverA, value); }
        public string DatabaseA { get => _databaseA; set => SetProperty(ref _databaseA, value); }
        public EnvironmentTag TagA { get => _tagA; set => SetProperty(ref _tagA, value); }
        public SolidColorBrush ColorA { get => _colorA; set => SetProperty(ref _colorA, value); }

        public string LabelB { get => _labelB; set => SetProperty(ref _labelB, value); }
        public string ServerB { get => _serverB; set => SetProperty(ref _serverB, value); }
        public string DatabaseB { get => _databaseB; set => SetProperty(ref _databaseB, value); }
        public EnvironmentTag TagB { get => _tagB; set => SetProperty(ref _tagB, value); }
        public SolidColorBrush ColorB { get => _colorB; set => SetProperty(ref _colorB, value); }

        public ICommand BeginComparisonCommand { get; }
        public ICommand BackCommand { get; }

        public event EventHandler BeginComparisonRequested;
        public event EventHandler BackRequested;

        public void PopulateFrom(ConnectionSideViewModel sideA, ConnectionSideViewModel sideB)
        {
            LabelA = sideA.Label;
            ServerA = sideA.ServerName;
            DatabaseA = sideA.DatabaseName;
            TagA = sideA.Tag;
            ColorA = EnvironmentTagColors.GetBrush(sideA.Tag);

            LabelB = sideB.Label;
            ServerB = sideB.ServerName;
            DatabaseB = sideB.DatabaseName;
            TagB = sideB.Tag;
            ColorB = EnvironmentTagColors.GetBrush(sideB.Tag);
        }
    }
}
