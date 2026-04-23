using System.Windows;
using SQLParity.Vsix.ViewModels;

namespace SQLParity.Vsix.Views
{
    public partial class GauntletFinalDialog : Window
    {
        public GauntletFinalDialog()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            (DataContext as GauntletViewModel)?.StartCountdown();
        }

        private void Proceed_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
