using System.Windows;

namespace SQLParity.Vsix.Views
{
    public partial class GauntletReviewDialog : Window
    {
        public GauntletReviewDialog()
        {
            InitializeComponent();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
