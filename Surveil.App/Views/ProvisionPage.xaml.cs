using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Surveil.App.ViewModels;

namespace Surveil.App.Views;

public sealed partial class ProvisionPage : Page
{
    private ProvisionViewModel Vm => (ProvisionViewModel)DataContext;

    public ProvisionPage()
    {
        InitializeComponent();
        DataContext = new ProvisionViewModel();
        Loaded += (_, _) => PasswordInput.Password = Vm.Password;
    }

    // PasswordBox.Password can't be two-way bound (by design), so mirror it into the VM here.
    private void OnPasswordChanged(object sender, RoutedEventArgs e) => Vm.Password = PasswordInput.Password;
}
