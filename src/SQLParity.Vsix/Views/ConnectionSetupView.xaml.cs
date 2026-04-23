using System.Windows;
using System.Windows.Controls;
using SQLParity.Vsix.ViewModels;

namespace SQLParity.Vsix.Views
{
    public partial class ConnectionSetupView : UserControl
    {
        public ConnectionSetupView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // PasswordBox can't be data-bound, so wire it manually (bidirectional)
            PasswordA.PasswordChanged += (s, _) =>
            {
                var vm = DataContext as ConnectionSetupViewModel;
                if (vm != null && vm.SideA.SqlPassword != PasswordA.Password)
                    vm.SideA.SqlPassword = PasswordA.Password;
            };
            PasswordB.PasswordChanged += (s, _) =>
            {
                var vm = DataContext as ConnectionSetupViewModel;
                if (vm != null && vm.SideB.SqlPassword != PasswordB.Password)
                    vm.SideB.SqlPassword = PasswordB.Password;
            };

            // Sync PasswordBox FROM VM when password is auto-filled from saved connections
            var setupVm = DataContext as ConnectionSetupViewModel;
            if (setupVm != null)
            {
                setupVm.SideA.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(ConnectionSideViewModel.SqlPassword)
                        && PasswordA.Password != setupVm.SideA.SqlPassword)
                        PasswordA.Password = setupVm.SideA.SqlPassword;
                };
                setupVm.SideB.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(ConnectionSideViewModel.SqlPassword)
                        && PasswordB.Password != setupVm.SideB.SqlPassword)
                        PasswordB.Password = setupVm.SideB.SqlPassword;
                };
            }
        }
    }
}
