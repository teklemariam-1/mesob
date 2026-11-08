using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Mesob.Studio;

public sealed partial class OverviewPage : Page
{
    public OverviewPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (App.Window?.StartupError is { } err) { await App.Message(Content.XamlRoot ?? XamlRoot, "Studio Control can't start", err); return; }
        var db = Studio.Db;
        NotReady.IsOpen = !Studio.IsReady;
        Eyebrow.Text = Studio.StudioCert != null ? $"STUDIO {Studio.StudioId}" : "STUDIO";
        var lastLog = db.Scalar("SELECT MAX(imported) FROM sales") as string;
        Subtitle.Text = $"{DateTime.Now:dddd d MMMM yyyy}" + (lastLog != null ? $"  ·  latest sales log received {lastLog}" : "  ·  no sales logs received yet");

        var sold = db.Long("SELECT COUNT(*) FROM sales WHERE kind='sale'");
        var retail = db.Long("SELECT COALESCE(SUM(price),0) FROM sales WHERE kind='sale'");
        var cashIn = db.Long("SELECT COALESCE(SUM(amount),0) FROM payments");
        var unlocked = db.Long("SELECT COALESCE(SUM(serial_count),0) FROM packs WHERE unlocked=1");
        var locked = db.Long("SELECT COALESCE(SUM(serial_count),0) FROM packs WHERE unlocked=0");
        var today = db.Long("SELECT COALESCE(SUM(cash),0) FROM reports WHERE received >= $1", DateTime.Now.ToString("yyyy-MM-dd"));
        var shops = db.Long("SELECT COUNT(*) FROM shops");
        Tiles.ItemsSource = new List<Tile>
        {
            new("CASH RECEIVED", $"{cashIn:N0}", "Nakfa from distributors for credit packs", ""),
            new("LICENSES SOLD", $"{sold:N0}", $"{retail:N0} Nakfa at shop prices", ""),
            new("CREDITS IN SHOPS", $"{Math.Max(0, unlocked - sold):N0}", locked > 0 ? $"{locked:N0} more sent, waiting for payment" : "paid and ready to sell", ""),
            new("CAPACITY LEFT", $"{Studio.CapacityLeft():N0}", "licenses you can still issue", ""),
            new("REPORTED TODAY", $"{today:N0}", "Nakfa, from shops' daily codes", ""),
            new("SHOPS", $"{shops}", $"{db.Long("SELECT COUNT(*) FROM shops WHERE blocked=1")} blocked", ""),
        };

        var alerts = db.Rows("SELECT * FROM alerts ORDER BY id DESC LIMIT 20").Select(a => new Row((string)a["text"]!, $"Raised {a["created"]}")).ToList();
        Alerts.ItemsSource = alerts;
        NoAlerts.IsOpen = alerts.Count == 0;

        var top = db.Rows("""
            SELECT t.title_id, t.title_en, t.title_ti, t.artist, t.kind, t.size, t.price, COUNT(s.serial) n, COALESCE(SUM(s.price),0) cash
            FROM titles t LEFT JOIN sales s ON s.title_id=t.title_id AND s.kind='sale'
            GROUP BY t.title_id ORDER BY cash DESC, n DESC LIMIT 6
            """);
        var max = Math.Max(1, top.Select(t => Convert.ToInt64(t["cash"])).DefaultIfEmpty(1).Max());
        Top.ItemsSource = top.Select((t, i) => new TitleRow
        {
            Id = (string)t["title_id"]!, TitleEn = (string)t["title_en"]!, TitleTi = t["title_ti"] as string ?? "", Kind = (string)t["kind"]!,
            Sold = Convert.ToInt64(t["n"]), Revenue = Convert.ToInt64(t["cash"]), Share = Convert.ToInt64(t["cash"]) * 100.0 / max, Rank = i + 1,
        }).ToList();

        Shops.ItemsSource = db.Rows("""
            SELECT sh.shop_id, sh.name, sh.town, COUNT(s.serial) sold, COALESCE(SUM(s.price),0) cash
            FROM shops sh LEFT JOIN sales s ON s.shop_id=sh.shop_id AND s.kind='sale' GROUP BY sh.shop_id ORDER BY cash DESC
            """).Select(s => new ShopRow
        {
            Id = Convert.ToInt64(s["shop_id"]), Name = (string)s["name"]!, Town = s["town"] as string ?? "",
            Sold = Convert.ToInt64(s["sold"]), Revenue = Convert.ToInt64(s["cash"]),
        }).ToList();
    }
}
