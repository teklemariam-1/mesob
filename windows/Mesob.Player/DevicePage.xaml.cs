using Mesob.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Mesob.Player;

public sealed partial class DevicePage : Page
{
    public DevicePage() => InitializeComponent();

    // The Player always runs in the dark theme; these match MesobGoodBrush / MesobWarnBrush in Theme.xaml.
    private static readonly Brush Good = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7F, 0xD4, 0x9B));
    private static readonly Brush Warn = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF2, 0xC1, 0x74));

    private static void Status(FontIcon icon, bool good)
    {
        icon.Glyph = good ? "" : "";
        icon.Foreground = good ? Good : Warn;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var fp = PlayerState.DeviceFingerprint;
        Fingerprint.Text = string.Join(" ", Enumerable.Range(0, fp.Length / 4).Select(i => fp.Substring(i * 4, 4).ToUpperInvariant()));

        var tpm = PlayerState.Store?.HardwareBacked == true;
        Status(KeyIcon, tpm);
        KeyTitle.Text = tpm ? "Device key locked by the TPM security chip" : "Device key locked by Windows data protection";
        KeyText.Text = tpm ? "The key that opens your titles can't be copied off this PC." : "This PC has no usable TPM 2.0 chip, so protection is weaker. Some shops may sell only music and books for it.";

        var capture = App.Window?.CaptureBlocked == true;
        Status(CaptureIcon, capture);
        CaptureTitle.Text = capture ? "Screen capture blocked" : "Screen capture can't be blocked";
        CaptureText.Text = capture ? "Screenshots, recordings and screen sharing show Mesob as blank." : "This Windows version can't hide Mesob from recordings, so protected titles won't play.";

        var info = MesobCore.Call("core.info");
        Build.Text = $"Mesob Player 0.1 · security core {info["version"]}{(MesobCore.IsLab ? " · lab build with development keys" : "")}";
        Folder.Text = $"Title files: {PlayerState.TitlesDir}";
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var pkg = new DataPackage();
        pkg.SetText(PlayerState.DeviceFingerprint.ToUpperInvariant());
        Clipboard.SetContent(pkg);
        CopyLabel.Text = "Copied";
        await Task.Delay(1500);
        CopyLabel.Text = "Copy";
    }
}
