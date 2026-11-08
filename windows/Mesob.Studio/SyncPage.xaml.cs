using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Mesob.Studio;

public sealed partial class SyncPage : Page
{
    private string? _root;

    public SyncPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Shops.ItemsSource = Studio.Db.Rows("SELECT shop_id, name, town FROM shops WHERE blocked=0 ORDER BY shop_id")
            .Select(s => new ShopRow { Id = Convert.ToInt64(s["shop_id"]), Name = (string)s["name"]!, Town = s["town"] as string ?? "" }).ToList();
    }

    private static bool Ok(string line) => !line.Contains("REJECTED") && !line.Contains("not written") && !line.StartsWith("No ");

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var picker = App.Picker(new FolderPicker());
        picker.FileTypeFilter.Add("*");
        var f = await picker.PickSingleFolderAsync();
        if (f == null) return;
        _root = f.Path;
        var drive = new DriveInfo(Path.GetPathRoot(f.Path)!);
        string free;
        try { free = $"  ·  {drive.AvailableFreeSpace / 1073741824.0:0.#} GB free"; } catch { free = ""; }
        var shops = Directory.Exists(Path.Combine(f.Path, "MESOB")) ? Directory.GetDirectories(Path.Combine(f.Path, "MESOB"), "from-shop-*").Length : 0;
        Stick.Text = $"{f.Path}{free}  ·  {(shops == 0 ? "no sales logs on it" : $"sales logs from {shops} shop(s)")}";
        ReadButton.IsEnabled = true;
        Shops_SelectionChanged(null!, null!);
    }

    private void Shops_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        WriteButton.IsEnabled = _root != null && Shops.SelectedItems.Count > 0;

    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        var root = _root!;
        ReadButton.IsEnabled = false;
        ReadBusy.Visibility = Visibility.Visible;
        try
        {
            var lines = await Task.Run(() => Studio.ReadReturns(root));
            ReadResults.ItemsSource = lines.Select(l => new StepResult(Ok(l), l)).ToList();
        }
        catch (Exception ex) { ReadResults.ItemsSource = new List<StepResult> { new(false, ex.Message) }; }
        finally { ReadButton.IsEnabled = true; ReadBusy.Visibility = Visibility.Collapsed; }
    }

    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        var root = _root!;
        var ids = Shops.SelectedItems.Cast<ShopRow>().Select(r => r.Id).ToList();
        WriteButton.IsEnabled = false;
        WriteBusy.Visibility = Visibility.Visible;
        var results = new List<StepResult>();
        try
        {
            foreach (var id in ids)
            {
                try { results.Add(new StepResult(true, await Task.Run(() => Studio.WriteSync(root, id)))); }
                catch (Exception ex) { results.Add(new StepResult(false, $"Shop {id}: not written, {ex.Message}")); }
                WriteResults.ItemsSource = results.ToList();
            }
        }
        finally { WriteBusy.Visibility = Visibility.Collapsed; Shops_SelectionChanged(null!, null!); }
    }
}
