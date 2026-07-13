using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Surveil.App.Services;
using Surveil.App.ViewModels;

namespace Surveil.App.Views;

/// <summary>The Configuration UI as a right-side drawer panel (reflowed vertically). Bound to the
/// shared <see cref="ConfigurationViewModel"/> so Scan/Discover can push targets into it.</summary>
public sealed partial class ConfigurationPanel : UserControl
{
    private ConfigurationViewModel Vm => (ConfigurationViewModel)DataContext;

    public ConfigurationPanel()
    {
        InitializeComponent();
    }

    public void Initialize(ConfigurationViewModel viewModel) => DataContext = viewModel;

    /// <summary>Applying the configuration writes to live cameras, so it's gated behind a confirmation
    /// that spells out exactly what will change on which cameras.</summary>
    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedCameraCount > 0)
        {
            var request = Vm.BuildRequest();

            var ips = Vm.SelectedIps;
            var shown = string.Join("\n", ips.Take(12).Select(ip => "   • " + ip));
            if (ips.Count > 12) shown += $"\n   … and {ips.Count - 12} more";

            var body = request.Actions.Count == 0
                ? "Nothing is set to change — fill in a field (Name, NTP, Codec…) first."
                : $"Apply {string.Join(", ", request.Actions)} to:\n\n{shown}";

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Write to {Vm.SelectedCameraCount} camera{(Vm.SelectedCameraCount == 1 ? "" : "s")}?",
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Write",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,  // default to Cancel — a live write should be deliberate
                IsPrimaryButtonEnabled = request.Options.HasChanges,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            await Vm.ApplyAsync(request);
            return;
        }

        await Vm.ApplyAsync();
    }

    /// <summary>Open the append-only configuration log (every change, success and failure) in the
    /// user's default text viewer.</summary>
    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = ConfigurationLog.FilePath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            if (!System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
        }
    }
}
