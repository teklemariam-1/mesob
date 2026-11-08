using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Mesob.Studio;

public sealed partial class ShopsPage : Page
{
    private string? _request;
    private List<ShopRow> _rows = [];

    public ShopsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private void Refresh()
    {
        _rows = Studio.Db.Rows("""
            SELECT s.*,
              (SELECT COUNT(*) FROM sales x WHERE x.shop_id=s.shop_id AND x.kind='sale') sold,
              (SELECT COALESCE(SUM(price),0) FROM sales x WHERE x.shop_id=s.shop_id AND x.kind='sale') cash,
              (SELECT COALESCE(SUM(serial_count),0) FROM packs p WHERE p.shop_id=s.shop_id AND p.unlocked=1) credits,
              (SELECT COALESCE(SUM(amount),0) FROM payments p WHERE p.shop_id=s.shop_id) paid
            FROM shops s ORDER BY shop_id
            """).Select(s => new ShopRow
        {
            Id = Convert.ToInt64(s["shop_id"]), Name = (string)s["name"]!, Town = s["town"] as string ?? "", Phone = s["phone"] as string ?? "",
            Sold = Convert.ToInt64(s["sold"]), Revenue = Convert.ToInt64(s["cash"]), Paid = Convert.ToInt64(s["paid"]),
            Credits = Math.Max(0, Convert.ToInt64(s["credits"]) - Convert.ToInt64(s["sold"])), Blocked = Convert.ToInt64(s["blocked"]) == 1,
            OnMore = More,
        }).ToList();
        List.ItemsSource = _rows;
        Empty.Visibility = Ui.Show(_rows.Count == 0);
        var max = Studio.StudioCert != null ? $" of {Studio.MaxShops} allowed by your licence" : "";
        Count.Text = $"{_rows.Count} shops{max}  ·  {_rows.Count(r => r.Blocked)} blocked";
    }

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        DialogError.Visibility = Visibility.Collapsed;
        SetupDialog.XamlRoot = XamlRoot;
        if (await SetupDialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var (id, file) = Studio.SetUpShop(_request!, ShopName.Text.Trim(), Town.Text.Trim(), Phone.Text.Trim());
            _request = null;
            Picked.Text = ShopName.Text = Town.Text = Phone.Text = "";
            Refresh();
            await App.Message(XamlRoot, $"Shop {id} set up",
                $"Saved {file}.\n\nNext: put the stick back in the tablet and choose Finish setup. Then make the shop a credit pack (Credits and codes) and write its first sync stick.");
        }
        catch (Exception ex) { await App.Message(XamlRoot, "Shop not set up", ex.Message); }
    }

    private void SetupDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var problem = _request == null ? "Choose the tablet's setup request." : string.IsNullOrWhiteSpace(ShopName.Text) ? "Enter the shop name." : null;
        if (problem == null) return;
        DialogError.Text = problem;
        DialogError.Visibility = Visibility.Visible;
        args.Cancel = true;
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var picker = App.Picker(new FileOpenPicker());
        picker.FileTypeFilter.Add(".json");
        var f = await picker.PickSingleFileAsync();
        if (f == null) return;
        _request = f.Path;
        Picked.Text = f.Path;
    }

    private void More(ShopRow shop, FrameworkElement btn)
    {
        var menu = new MenuFlyout();
        var block = new MenuFlyoutItem
        {
            Text = shop.Blocked ? "Unblock shop" : "Block shop…",
            Icon = new FontIcon { Glyph = shop.Blocked ? "" : "" },
        };
        block.Click += async (_, _) =>
        {
            if (!shop.Blocked && !await App.Confirm(XamlRoot, $"Block shop {shop.Id}?",
                    $"{shop.Name} gets no new titles, credits or sync sticks until you unblock it. Licenses it already sold keep working.", "Block")) return;
            Studio.Db.Exec("UPDATE shops SET blocked=$1 WHERE shop_id=$2", shop.Blocked ? 0 : 1, shop.Id);
            Refresh();
        };
        menu.Items.Add(block);
        menu.ShowAt(btn);
    }
}
