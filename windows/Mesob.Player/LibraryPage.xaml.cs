using System.Text.Json.Nodes;
using Mesob.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Mesob.Player;

public sealed partial class LibraryPage : Page
{
    public LibraryPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        Refresh();
        if (App.Window?.StartupError is { } err)
            await App.Message(XamlRoot ?? Content.XamlRoot, "Mesob Player can't start", err);
    }

    private string Kind => (Filter.SelectedItem?.Tag as string) ?? "all";

    private void Refresh()
    {
        var all = PlayerState.Library;
        var q = Search.Text?.Trim() ?? "";
        var shown = all
            .Where(i => Kind == "all" || i.Kind == Kind)
            .Where(i => q.Length == 0 || i.TitleEn.Contains(q, StringComparison.OrdinalIgnoreCase) || i.TitleTi.Contains(q) || i.Artist.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Locked).ThenBy(i => i.TitleEn)
            .ToList();
        Items.ItemsSource = shown;
        var owned = all.Count(i => !i.Locked);
        Summary.Text = all.Count == 0 ? "Nothing here yet." : $"{owned} {(owned == 1 ? "title" : "titles")} yours · {all.Count - owned} locked";
        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count > 0 && shown.Count == 0)
        {
            EmptyTitle.Text = "No matches";
            EmptyText.Text = "Try another filter or search word.";
        }
        else
        {
            EmptyTitle.Text = "Your library is empty";
            EmptyText.Text = "Buy at a Mesob shop over its Wi-Fi, or add title and license files from a USB stick. Files from friends stay locked until you buy them.";
        }
    }

    private void Filter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => Refresh();
    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        Info.Severity = severity;
        Info.Title = title;
        Info.Message = message;
        Info.IsOpen = true;
    }

    private async void AddTitles_Click(object sender, RoutedEventArgs e)
    {
        var picker = App.Picker(new FileOpenPicker());
        picker.FileTypeFilter.Add(".msb");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        int ok = 0;
        var problems = new List<string>();
        foreach (var f in files)
        {
            try { await Task.Run(() => PlayerState.ImportTitle(f.Path)); ok++; }
            catch (Exception ex) { problems.Add($"{f.Name}: {ex.Message}"); }
        }
        Refresh();
        if (problems.Count > 0) Show(InfoBarSeverity.Warning, $"Added {ok} of {files.Count} titles", string.Join("  ·  ", problems));
        else Show(InfoBarSeverity.Success, $"Added {ok} {(ok == 1 ? "title" : "titles")}", "Titles without a license show as locked until you buy them.");
    }

    private async void AddLicenses_Click(object sender, RoutedEventArgs e)
    {
        var picker = App.Picker(new FileOpenPicker());
        picker.FileTypeFilter.Add(".msl");
        picker.FileTypeFilter.Add(".msls");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        int ok = 0;
        var problems = new List<string>();
        foreach (var f in files)
            foreach (var b in PlayerState.ReadLicenseFile(await File.ReadAllBytesAsync(f.Path)))
            {
                try { PlayerState.ImportLicense(b); ok++; }
                catch (Exception ex) { problems.Add($"{f.Name}: {ex.Message}"); }
            }
        Refresh();
        if (problems.Count > 0) Show(InfoBarSeverity.Warning, $"{ok} licenses added, {problems.Count} refused", string.Join("  ·  ", problems));
        else Show(InfoBarSeverity.Success, $"{ok} {(ok == 1 ? "license" : "licenses")} added", "Licensed titles are ready to play.");
    }

    private async void MakeRequest_Click(object sender, RoutedEventArgs e)
    {
        var locked = PlayerState.Library.Where(i => i.Locked).Take(20).ToList();
        if (locked.Count == 0)
        {
            Show(InfoBarSeverity.Informational, "Nothing to request", "Every title in your library is already yours. Add the title files you want to buy first.");
            return;
        }
        var ids = new JsonArray(locked.Select(i => (JsonNode)i.TitleId).ToArray());
        var req = MesobCore.Call("request.make", new JsonObject { ["device_secret"] = PlayerState.DeviceSecret, ["title_ids"] = ids });
        var picker = App.Picker(new FileSavePicker { SuggestedFileName = "mesob-request" });
        picker.FileTypeChoices.Add("Mesob request", [".msr"]);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        await File.WriteAllBytesAsync(file.Path, Convert.FromBase64String(req["request"]!.GetValue<string>()));
        Show(InfoBarSeverity.Success, $"Request saved for {locked.Count} {(locked.Count == 1 ? "title" : "titles")}",
            "Take the USB stick to a Mesob shop. After you pay, they write your licenses back to it; then choose Add → License files.");
    }

    private void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        var item = (LibraryItem)e.ClickedItem;
        if (item.Locked)
        {
            Show(InfoBarSeverity.Informational, $"{item.TitleEn} is locked",
                "Buy it at any Mesob shop: open Shop while on the shop's Wi-Fi, or make a request file and take it on a USB stick.");
            return;
        }
        App.Window!.Open(item);
    }
}
