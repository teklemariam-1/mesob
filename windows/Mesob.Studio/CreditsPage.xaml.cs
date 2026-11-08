using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Mesob.Studio;

public sealed partial class CreditsPage : Page
{
    private List<PackRow> _packs = [];

    public CreditsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private void Refresh()
    {
        var shops = Studio.Db.Rows("SELECT shop_id, name FROM shops WHERE blocked=0 ORDER BY shop_id")
            .Select(s => new Row($"Shop {s["shop_id"]} · {s["name"]}", "", "", Convert.ToInt64(s["shop_id"]))).ToList();
        Shop.ItemsSource = shops;
        ReportShop.ItemsSource = shops;
        if (shops.Count > 0) { Shop.SelectedIndex = 0; ReportShop.SelectedIndex = 0; }

        _packs = Studio.Db.Rows("""
            SELECT p.*, s.name,
              (SELECT COUNT(*) FROM sales x WHERE x.shop_id=p.shop_id AND x.kind='sale' AND x.serial >= p.serial_start AND x.serial < p.serial_start + p.serial_count) used
            FROM packs p JOIN shops s ON s.shop_id=p.shop_id ORDER BY p.unlocked, p.pack_id DESC
            """).Select(p => new PackRow
        {
            Id = Convert.ToInt64(p["pack_id"]), ShopId = Convert.ToInt64(p["shop_id"]), ShopName = (string)p["name"]!,
            Start = Convert.ToInt64(p["serial_start"]), Count = Convert.ToInt64(p["serial_count"]), Used = Convert.ToInt64(p["used"]),
            Value = Convert.ToInt64(p["serial_count"]) * Convert.ToInt64(p["wholesale"]), Unlocked = Convert.ToInt64(p["unlocked"]) == 1,
            OnAction = (pack, show) => { if (show) ShowCode(pack); else Unlock(pack); },
        }).ToList();
        Packs.ItemsSource = _packs;
        Empty.Visibility = Ui.Show(_packs.Count == 0);

        var waiting = _packs.Where(p => !p.Unlocked).ToList();
        Capacity.Text = $"{Studio.CapacityLeft():N0} licenses of vendor capacity left"
            + (waiting.Count > 0 ? $"  ·  {waiting.Count} pack(s) waiting for payment, {Ui.Nakfa(waiting.Sum(p => p.Value))}" : "  ·  every pack is paid");
        Pack_ValueChanged(null!, null!);
    }

    private void Pack_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (PackValue == null || double.IsNaN(Size.Value) || double.IsNaN(Wholesale.Value)) return;
        PackValue.Text = $"The distributor pays you {Ui.Nakfa((long)Size.Value * (long)Wholesale.Value)} for this pack.";
    }

    private async void MakePack_Click(object sender, RoutedEventArgs e)
    {
        if (Shop.SelectedItem is not Row shop) { await App.Message(XamlRoot, "No shop", "Set up a shop first."); return; }
        try
        {
            var id = Studio.CreatePack((long)shop.Tag!, (long)Size.Value, (long)Wholesale.Value);
            Refresh();
            await App.Message(XamlRoot, $"Pack {id} made", "It goes to the shop, locked, on the next sync stick. Unlock it when the distributor pays.");
        }
        catch (Exception ex) { await App.Message(XamlRoot, "Pack not made", ex.Message); }
    }

    private async void Unlock(PackRow pack)
    {
        UnlockInfo.Text = $"{pack.IdLabel} for {pack.ShopLabel}: {pack.Count:N0} licenses, {pack.ValueLabel}. Record what you received, then read the code to the distributor.";
        Cash.Value = pack.Value;
        Note.Text = "";
        UnlockDialog.XamlRoot = XamlRoot;
        if (await UnlockDialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var code = Studio.Unlock(pack.Id, double.IsNaN(Cash.Value) ? 0 : (long)Cash.Value, Note.Text.Trim());
            Refresh();
            await App.ShowCode(XamlRoot, $"Unlock code for {pack.IdLabel}", code,
                $"The shop enters this on its tablet (Credits → Unlock credits). It only works for {pack.ShopLabel}, pack {pack.Id}.");
        }
        catch (Exception ex) { await App.Message(XamlRoot, "No code made", ex.Message); }
    }

    private async void ShowCode(PackRow pack)
    {
        try
        {
            await App.ShowCode(XamlRoot, $"Unlock code for {pack.IdLabel}", Studio.CodeFor(pack.Id),
                $"Already paid. Give this again if {pack.ShopName} lost it; no cash is recorded.");
        }
        catch (Exception ex) { await App.Message(XamlRoot, "No code", ex.Message); }
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (ReportShop.SelectedItem is not Row shop) return;
        try
        {
            ReportOut.Message = Studio.EnterReport((long)shop.Tag!, ReportCode.Text);
            ReportOut.Title = "Code accepted";
            ReportOut.Severity = InfoBarSeverity.Success;
            ReportCode.Text = "";
        }
        catch (Exception ex)
        {
            ReportOut.Title = "Not accepted";
            ReportOut.Message = $"{ex.Message}. Check the shop and the characters.";
            ReportOut.Severity = InfoBarSeverity.Error;
        }
        ReportOut.IsOpen = true;
    }
}
