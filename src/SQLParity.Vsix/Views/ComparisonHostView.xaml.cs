using System.Windows;
using System.Windows.Controls;
using SQLParity.Vsix.ViewModels;

namespace SQLParity.Vsix.Views
{
    public partial class ComparisonHostView : UserControl
    {
        public ComparisonHostView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            if (DataContext is ComparisonHostViewModel vm)
                SubscribeGauntlet(vm);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ComparisonHostViewModel oldVm)
                oldVm.GauntletRequested -= OnGauntletRequested;

            if (e.NewValue is ComparisonHostViewModel newVm)
                SubscribeGauntlet(newVm);
        }

        private void SubscribeGauntlet(ComparisonHostViewModel vm)
        {
            vm.GauntletRequested += OnGauntletRequested;
        }

        private void OnGauntletRequested(object sender, GauntletRequestedEventArgs args)
        {
            var gauntletVm = new GauntletViewModel();
            gauntletVm.Populate(args.SelectedChanges, args.DestinationLabel, args.DestinationTag);

            var reviewDialog = new GauntletReviewDialog
            {
                DataContext = gauntletVm,
                Owner = Window.GetWindow(this)
            };

            if (reviewDialog.ShowDialog() != true)
                return;

            var finalDialog = new GauntletFinalDialog
            {
                DataContext = gauntletVm,
                Owner = Window.GetWindow(this)
            };

            if (finalDialog.ShowDialog() == true)
                args.Confirmed = true;
        }
    }
}
