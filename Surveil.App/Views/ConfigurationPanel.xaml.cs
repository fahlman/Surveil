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
        DataContext = AppSession.Current.Configuration;
    }

    /// <summary>Applying the configuration writes to live cameras, so it's gated behind a confirmation
    /// that spells out exactly what will change on which cameras.</summary>
    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedCameraCount > 0)
        {
            var actions = new List<string>();
            if (Vm.SingleCameraSelected && !string.IsNullOrWhiteSpace(Vm.Name)) actions.Add($"name = {Vm.Name.Trim()}");
            if (Vm.SingleCameraSelected && !string.IsNullOrWhiteSpace(Vm.Hostname)) actions.Add($"hostname = {Vm.Hostname.Trim()}");
            if (!string.IsNullOrWhiteSpace(Vm.NtpServer)) actions.Add($"NTP server = {Vm.NtpServer.Trim()}");
            if (Vm.SelectedTimeZone is { LeaveUnchanged: false } tz) actions.Add($"time zone = {tz.Label}");
            if (Vm.ShowVideoSection && Vm.SelectedCodec != "Leave unchanged") actions.Add($"codec = {Vm.SelectedCodec}");
            if (Vm.ShowVideoSection && Vm.SelectedResolution?.Resolution is { } r) actions.Add($"resolution = {r.Width}×{r.Height}");
            if (Vm.ShowVideoSection && Vm.SelectedFrameRate?.Fps is { } fps) actions.Add($"frame rate = {System.Math.Round(fps)} fps");
            if (Vm.ShowVideoSection && Vm.CanSetBitrate && !double.IsNaN(Vm.BitrateKbps) && Vm.BitrateKbps > 0)
                actions.Add($"bitrate = {(int)System.Math.Round(Vm.BitrateKbps)} kbps");

            var ips = Vm.SelectedIps;
            var shown = string.Join("\n", ips.Take(12).Select(ip => "   • " + ip));
            if (ips.Count > 12) shown += $"\n   … and {ips.Count - 12} more";

            var body = actions.Count == 0
                ? "Nothing is set to change — fill in a field (Name, NTP, Codec…) first."
                : $"Apply {string.Join(", ", actions)} to:\n\n{shown}";

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Write to {Vm.SelectedCameraCount} camera{(Vm.SelectedCameraCount == 1 ? "" : "s")}?",
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Write",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,  // default to Cancel — a live write should be deliberate
                IsPrimaryButtonEnabled = actions.Count > 0,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
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
