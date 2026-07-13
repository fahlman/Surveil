using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Surveil.App.Services;
using Surveil.App.ViewModels;

namespace Surveil.App.Views;

/// <summary>The Provision UI as a right-side drawer panel (reflowed vertically). Bound to the
/// shared <see cref="ProvisionViewModel"/> so Scan/Discover can push targets into it.</summary>
public sealed partial class ProvisionPanel : UserControl
{
    private ProvisionViewModel Vm => (ProvisionViewModel)DataContext;

    public ProvisionPanel()
    {
        InitializeComponent();
        DataContext = AppSession.Current.Provision;
        Loaded += (_, _) => PasswordInput.Password = Vm.Password;
    }

    // PasswordBox.Password can't be two-way bound (by design), so mirror it into the VM here.
    private void OnPasswordChanged(object sender, RoutedEventArgs e) => Vm.Password = PasswordInput.Password;

    /// <summary>A real (non-dry-run) write to live cameras is gated behind a confirmation that spells
    /// out exactly what will change on which cameras.</summary>
    private async void OnProvisionClick(object sender, RoutedEventArgs e)
    {
        if (!Vm.DryRun && Vm.SelectedCameraCount > 0)
        {
            var actions = new List<string>();
            if (Vm.SetName) actions.Add("name");
            if (Vm.SetHostname) actions.Add("hostname");
            if (Vm.SetNtp) actions.Add(string.IsNullOrWhiteSpace(Vm.NtpPosixTimeZone) ? "NTP (this PC's zone)" : $"NTP ({Vm.NtpPosixTimeZone})");
            if (Vm.SetVideo && Vm.ShowVideoSection)
                actions.Add($"video ({Vm.SelectedCodec}, up to {(Vm.SelectedResolution?.Resolution is { } r ? $"{r.Width}×{r.Height}" : "highest")})");

            var ips = Vm.SelectedIps;
            var shown = string.Join("\n", ips.Take(12).Select(ip => "   • " + ip));
            if (ips.Count > 12) shown += $"\n   … and {ips.Count - 12} more";

            var body = actions.Count == 0
                ? "Nothing is selected to change — tick Set name, Set hostname, or Set NTP first."
                : $"This is NOT a dry run. Will set {string.Join(", ", actions)} on:\n\n{shown}";

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Write to {Vm.SelectedCameraCount} camera{(Vm.SelectedCameraCount == 1 ? "" : "s")}?",
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Write",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,  // default to Cancel — a live write should be deliberate
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        await Vm.ProvisionAsync();
    }
}
