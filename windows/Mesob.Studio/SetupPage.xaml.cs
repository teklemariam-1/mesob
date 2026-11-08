using Mesob.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Mesob.Studio;

public sealed partial class SetupPage : Page
{
    public SetupPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (App.Window?.StartupError is { } err) { await App.Message(Content.XamlRoot ?? XamlRoot, "Studio Control can't start", err); return; }
        Refresh();
    }

    private void Step(Border badge, FontIcon icon, bool done, string glyph)
    {
        badge.Background = (Brush)Application.Current.Resources[done ? "MesobGoodBackground" : "SubtleFillColorSecondaryBrush"];
        icon.Foreground = (Brush)Application.Current.Resources[done ? "MesobGoodBrush" : "TextFillColorSecondaryBrush"];
        icon.Glyph = done ? "" : glyph;
    }

    private void Refresh()
    {
        var caps = Studio.Db.Long("SELECT COUNT(*) FROM capacity");
        Step(KeyBadge, KeyIcon, Studio.HasKey, "");
        Step(CertBadge, CertIcon, Studio.StudioCert != null, "");
        Step(CapBadge, CapIcon, caps > 0, "");

        KeyState.Text = Studio.HasKey ? $"Public key {Studio.StudioPublicHex()}" : "Not created yet. Create it here; it never leaves this PC.";
        CreateKeyButton.Visibility = Ui.Show(!Studio.HasKey);
        SaveRequestButton.Visibility = Ui.Show(Studio.HasKey && Studio.StudioCert == null);
        CertState.Text = Studio.StudioCert != null
            ? $"Studio {Studio.StudioId}, licensed for up to {Studio.MaxShops} shops."
            : "Send the vendor your request file; they return studio.cert.";
        CertButton.Content = Studio.StudioCert != null ? "Replace…" : "Import certificate…";
        CapState.Text = caps == 0
            ? "No capacity yet. Each capacity block lets you issue a fixed number of licenses."
            : $"{Studio.CapacityLeft():N0} licenses left across {caps} block(s). Import more any time.";

        var hw = Studio.Store.HardwareBacked;
        Protection.Severity = hw ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        Protection.Title = hw ? "Keys are locked by this PC's TPM chip" : "No TPM 2.0 chip found";
        Protection.Message = hw
            ? "The studio key and title keys can't be copied off this PC, even with the database."
            : "Keys are locked by Windows data protection only. Use the vendor-supplied Studio PC.";
        LabNote.IsOpen = MesobCore.IsLab;
        App.Window?.RefreshBadge();
    }

    private async void CreateKey_Click(object sender, RoutedEventArgs e)
    {
        try { Studio.CreateKey(); Refresh(); }
        catch (Exception ex) { await App.Message(XamlRoot, "Studio key not created", ex.Message); }
    }

    private async void SaveRequest_Click(object sender, RoutedEventArgs e)
    {
        var picker = App.Picker(new FileSavePicker { SuggestedFileName = "studio-request" });
        picker.FileTypeChoices.Add("Text", [".txt"]);
        var f = await picker.PickSaveFileAsync();
        if (f == null) return;
        await File.WriteAllTextAsync(f.Path, Studio.StudioPublicHex());
        await App.Message(XamlRoot, "Request saved", "Send this file to the vendor. They will return a studio certificate and a capacity block.");
    }

    private async void ImportCert_Click(object sender, RoutedEventArgs e)
    {
        var picker = App.Picker(new FileOpenPicker());
        picker.FileTypeFilter.Add(".cert");
        picker.FileTypeFilter.Add(".bin");
        var f = await picker.PickSingleFileAsync();
        if (f == null) return;
        try { Studio.ImportStudioCert(await File.ReadAllBytesAsync(f.Path)); Refresh(); }
        catch (Exception ex) { await App.Message(XamlRoot, "Certificate not imported", ex.Message); }
    }

    private async void ImportCap_Click(object sender, RoutedEventArgs e)
    {
        var picker = App.Picker(new FileOpenPicker());
        picker.FileTypeFilter.Add(".bin");
        var files = await picker.PickMultipleFilesAsync();
        var msgs = new List<string>();
        foreach (var f in files)
        {
            try { msgs.Add(Studio.ImportCapacity(await File.ReadAllBytesAsync(f.Path))); }
            catch (Exception ex) { msgs.Add($"{f.Name}: {ex.Message}"); }
        }
        Refresh();
        if (msgs.Count > 0) await App.Message(XamlRoot, "Capacity", string.Join("\n", msgs));
    }
}
