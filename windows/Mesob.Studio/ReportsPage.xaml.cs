using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Mesob.Studio;

public sealed partial class ReportsPage : Page
{
    private List<Dictionary<string, object?>> _rows = [];

    public ReportsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => Load();
    private void Kind_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => Load();

    private string Mode => (Kind.SelectedItem?.Tag as string) ?? "shop";
    private string ModeName => Kind.SelectedItem?.Text ?? "By shop";

    private static long L(object? v) => Convert.ToInt64(v ?? 0);

    private void Load()
    {
        if (List == null) return;
        var db = Studio.Db;
        (string head, string sql, Func<Dictionary<string, object?>, Row> map) report = Mode switch
        {
            "title" => ("TITLE", """
                SELECT t.title_en title, t.title_ti tigrinya, t.kind, COUNT(s.serial) sold, COALESCE(SUM(s.price),0) nakfa
                FROM titles t LEFT JOIN sales s ON s.title_id=t.title_id AND s.kind='sale' GROUP BY t.title_id ORDER BY nakfa DESC, sold DESC
                """, r => new Row($"{r["title"]}", $"{Ui.KindLabel((string)r["kind"]!)}  ·  {r["tigrinya"]}  ·  {L(r["sold"]):N0} sold", $"{L(r["nakfa"]):N0}")),
            "day" => ("DAY", """
                SELECT date(s.imported) imported_on, s.shop_id shop, sh.name, COUNT(*) sold, SUM(s.price) nakfa
                FROM sales s JOIN shops sh ON sh.shop_id=s.shop_id WHERE s.kind='sale' GROUP BY s.shop_id, date(s.imported) ORDER BY imported_on DESC, shop
                """, r => new Row($"{r["imported_on"]}", $"Shop {r["shop"]} · {r["name"]}  ·  {L(r["sold"]):N0} sold", $"{L(r["nakfa"]):N0}")),
            "codes" => ("SHOP AND DAY", "SELECT r.shop_id shop, sh.name, r.day, r.count sold, r.cash nakfa, r.log_tag, r.received FROM reports r JOIN shops sh ON sh.shop_id=r.shop_id ORDER BY r.received DESC",
                r => new Row($"Shop {r["shop"]} · {r["name"]}", $"Day {r["day"]}  ·  {L(r["sold"]):N0} sales  ·  received {r["received"]}  ·  log {r["log_tag"]}", $"{L(r["nakfa"]):N0}")),
            "log" => ("SALE", """
                SELECT s.shop_id shop, s.seq, s.kind, s.serial, t.title_en title, s.device, s.price nakfa, s.imported
                FROM sales s LEFT JOIN titles t ON t.title_id=s.title_id ORDER BY s.imported DESC, s.shop_id, s.seq DESC LIMIT 2000
                """, r => new Row($"{r["title"]}", $"Shop {r["shop"]} #{r["seq"]}  ·  {r["kind"]}  ·  serial {r["serial"]}  ·  device {r["device"]}", $"{L(r["nakfa"]):N0}")),
            "cash" => ("SHOP", "SELECT p.created, p.shop_id shop, sh.name, p.amount nakfa, p.note FROM payments p JOIN shops sh ON sh.shop_id=p.shop_id ORDER BY p.id DESC",
                r => new Row($"Shop {r["shop"]} · {r["name"]}", $"{r["created"]}  ·  {r["note"]}", $"{L(r["nakfa"]):N0}")),
            _ => ("SHOP", """
                SELECT sh.shop_id shop, sh.name, sh.town, COUNT(s.serial) sold, COALESCE(SUM(s.price),0) nakfa,
                  (SELECT COALESCE(SUM(amount),0) FROM payments p WHERE p.shop_id=sh.shop_id) paid
                FROM shops sh LEFT JOIN sales s ON s.shop_id=sh.shop_id AND s.kind='sale' GROUP BY sh.shop_id ORDER BY nakfa DESC
                """, r => new Row($"Shop {r["shop"]} · {r["name"]}", $"{r["town"]}  ·  {L(r["sold"]):N0} sold  ·  paid the studio {L(r["paid"]):N0}", $"{L(r["nakfa"]):N0}")),
        };
        _rows = db.Rows(report.sql);
        List.ItemsSource = _rows.Select(report.map).ToList();
        HeadA.Text = report.head;
        Empty.Visibility = Ui.Show(_rows.Count == 0);
        var total = _rows.Sum(r => L(r["nakfa"]));
        Total.Text = $"{total:N0}";
        TotalLabel.Text = Mode == "log" && _rows.Count == 2000 ? "Total of the latest 2,000 entries" : $"Total  ·  {_rows.Count:N0} rows";
        Summary.Text = $"{ModeName}  ·  from signed sales logs and codes received so far";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = App.Picker(new FileSavePicker { SuggestedFileName = $"mesob-{Mode}-{DateTime.Now:yyyy-MM-dd}" });
        picker.FileTypeChoices.Add("CSV (opens in Excel)", [".csv"]);
        var f = await picker.PickSaveFileAsync();
        if (f == null) return;
        var lines = new List<string>();
        if (_rows.Count > 0) lines.Add(string.Join(",", _rows[0].Keys.Select(Studio.CsvEscape)));
        lines.AddRange(_rows.Select(r => string.Join(",", r.Values.Select(Studio.CsvEscape))));
        await File.WriteAllLinesAsync(f.Path, lines, new System.Text.UTF8Encoding(true));
        await App.Message(XamlRoot, "Exported", $"{_rows.Count} rows saved to {f.Path}.");
    }
}
