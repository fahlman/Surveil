using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Surveil.App.ViewModels;

namespace Surveil.App.Views;

/// <summary>The Provision UI as a right-side drawer panel (reflowed vertically). Hosts its own
/// <see cref="ProvisionViewModel"/>, so its state persists while the drawer is toggled.</summary>
public sealed partial class ProvisionPanel : UserControl
{
    private ProvisionViewModel Vm => (ProvisionViewModel)DataContext;

    public ProvisionPanel()
    {
        InitializeComponent();
        DataContext = new ProvisionViewModel();
        Loaded += (_, _) => PasswordInput.Password = Vm.Password;
    }

    // PasswordBox.Password can't be two-way bound (by design), so mirror it into the VM here.
    private void OnPasswordChanged(object sender, RoutedEventArgs e) => Vm.Password = PasswordInput.Password;
}
