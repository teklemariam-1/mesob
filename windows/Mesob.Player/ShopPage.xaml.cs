using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using Mesob.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Streams;

namespace Mesob.Player;

public sealed class CatalogItem : INotifyPropertyChanged
{
    public string TitleId { get; init; } = "";
    public string TitleEn { get; init; } = "";
    public string TitleTi { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Kind { get; init; } = "";
    public long Size { get; init; }
    public long Price { get; init; }
    private bool _owned;
    public bool Owned
    {
        get => _owned;
        set { _owned = value; Changed(nameof(OwnedVisibility)); Changed(nameof(AddVisibility)); Changed(nameof(InCartVisibility)); }
    }
    public Action<CatalogItem>? OnToggle { get; init; }

    private BitmapImage? _poster;
    public BitmapImage? Poster
    {
        get => _poster;
        set { _poster = value; Changed(nameof(Poster)); }
    }

    private bool _inCart;
    public bool InCart
    {
        get => _inCart;
        set { _inCart = value; Changed(nameof(InCartVisibility)); Changed(nameof(AddVisibility)); }
    }

    public string KindGlyph => Kind switch { "audio" => "", "pdf" => "", _ => "" };
    public string KindLabel => Kind switch { "audio" => "MUSIC", "pdf" => "BOOK", _ => "VIDEO" };
    public string Byline => string.Join(" · ", new[] { Artist, Size >= 1048576 ? $"{Size / 1048576.0:0.#} MB" : $"{Math.Max(1, Size / 1024)} KB" }.Where(s => s != ""));
    public string PriceLabel => $"{Price:N0} Nakfa";
    public Visibility OwnedVisibility => Owned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AddVisibility => !Owned && !InCart ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InCartVisibility => !Owned && InCart ? Visibility.Visible : Visibility.Collapsed;

    public StoreCart.Line ToLine() => new(TitleId, TitleEn, TitleTi, Artist, Kind, Price);
    public void Toggle() => OnToggle?.Invoke(this);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One line in the cart panel or on the pickup-code screen.</summary>
public sealed class CartLine(StoreCart.Line line, BitmapImage? picture, Action<string>? onRemove)
{
    public string Label => line.TitleEn;
    public string Amount => $"{line.Price:N0} Nakfa";
    public string Glyph => line.Kind switch { "audio" => "", "pdf" => "", _ => "" };
    public BitmapImage? Picture => picture;
    public void Remove() => onRemove?.Invoke(line.Id);
}

/// <summary>
/// The in-shop store, Kindle style: browse and search the shop's catalog over its Wi-Fi, fill a
/// cart, place the order, show the pickup code at the counter, and the titles arrive in the
/// library once the cashier marks it paid. Same flow as the phone app and MediaVault's checkout.
/// </summary>
public sealed partial class ShopPage : Page
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private Uri? _base;
    private string _shopName = "";
    private List<CatalogItem> _items = [];
    private CancellationTokenSource? _poll;

    public ShopPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        StoreCart.Load();
        AddressBox.Text = StoreCart.LastAddress;
        if (StoreCart.LastShopName != "") ConnText.Text = $"Last time you shopped at {StoreCart.LastShopName}. Join its Wi-Fi and connect, or enter another shop's address.";
        if (StoreCart.Lines.Count > 0) NotConnectedText.Text = $"Your cart is saved: {StoreCart.Lines.Count} title(s), {StoreCart.Total:N0} Nakfa. Connect to the shop to order.";
        if (StoreCart.Order != null) ShowOrder(StoreCart.Order);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _poll?.Cancel();

    // ---------- Connecting and browsing ----------

    private void Address_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Connect_Click(sender, e);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        ConnTitle.Text = "Connecting…";
        try
        {
            var addr = AddressBox.Text.Trim();
            _base = ShopProtocol.BaseUri(addr);
            var cat = await Http.GetFromJsonAsync<JsonObject>(new Uri(_base, "api/catalog")) ?? throw new IOException("empty catalog");
            _shopName = cat["shop_name"]?.GetValue<string>() ?? $"Shop {cat["shop_id"]}";
            var owned = PlayerState.Library.Where(i => !i.Locked).Select(i => i.TitleId).ToHashSet();
            _items = cat["titles"]!.AsArray().Select(t => new CatalogItem
            {
                TitleId = t!["title_id"]!.GetValue<string>(),
                TitleEn = t["title_en"]!.GetValue<string>(),
                TitleTi = t["title_ti"]?.GetValue<string>() ?? "",
                Artist = t["artist"]?.GetValue<string>() ?? "",
                Kind = t["kind"]!.GetValue<string>(),
                Size = t["size"]!.GetValue<long>(),
                Price = t["price"]!.GetValue<long>(),
                Owned = owned.Contains(t["title_id"]!.GetValue<string>()),
                OnToggle = ToggleItem,
            }).ToList();
            var dropped = StoreCart.AtShop(addr, _shopName, _items.Where(i => !i.Owned).Select(i => i.TitleId).ToHashSet());
            foreach (var i in _items) i.InCart = StoreCart.Contains(i.TitleId);

            ShopHeading.Text = _shopName;
            ShopSub.Text = $"{_items.Count} titles  ·  prices set by the studio  ·  pay cash at the counter"
                + (dropped > 0 ? $"  ·  {dropped} cart item(s) taken out (already yours, or not sold here)" : "");
            ConnectCard.Visibility = Visibility.Collapsed;
            LeaveButton.Visibility = Visibility.Visible;
            Toolbar.Visibility = Visibility.Visible;
            NotConnected.Visibility = Visibility.Collapsed;
            CatalogGrid.Visibility = Visibility.Visible;
            CartPanel.Visibility = Visibility.Visible;
            CartColumn.Width = new GridLength(360);
            ApplyFilter();
            RefreshCart();
            foreach (var item in _items) _ = LoadPoster(item);
        }
        catch (Exception ex)
        {
            ConnTitle.Text = "Can't reach the shop";
            ConnText.Text = $"Check you're on the shop's Wi-Fi and the address is right. ({ex.Message})";
            ConnIcon.Glyph = "";
        }
        finally { ConnectButton.IsEnabled = true; }
    }

    private void Leave_Click(object sender, RoutedEventArgs e)
    {
        _base = null;
        _items = [];
        ShopHeading.Text = "Shop";
        ShopSub.Text = "Browse what the shop sells, fill your cart, and pay cash at the counter.";
        ConnTitle.Text = "Connect to a shop";
        ConnIcon.Glyph = "";
        ConnectCard.Visibility = Visibility.Visible;
        LeaveButton.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        CatalogGrid.Visibility = Visibility.Collapsed;
        CartPanel.Visibility = Visibility.Collapsed;
        CartColumn.Width = new GridLength(0);
        NoMatch.Visibility = Visibility.Collapsed;
        NotConnected.Visibility = Visibility.Visible;
    }

    private async Task LoadPoster(CatalogItem item)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(new Uri(_base!, $"api/poster/{item.TitleId}"));
            using var mem = new InMemoryRandomAccessStream();
            await mem.WriteAsync(bytes.AsBuffer());
            mem.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(mem);
            item.Poster = bmp;
            if (StoreCart.Contains(item.TitleId)) RefreshCart();
        }
        catch { /* no poster for this title */ }
    }

    private void KindFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ApplyFilter();
    private void StoreSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void ApplyFilter()
    {
        if (CatalogGrid == null) return;
        var kind = KindFilter.SelectedItem?.Tag as string ?? "all";
        var q = StoreSearch.Text.Trim();
        var shown = _items.Where(i => (kind == "all" || i.Kind == kind) &&
            (q == "" || i.TitleEn.Contains(q, StringComparison.OrdinalIgnoreCase) || i.TitleTi.Contains(q) || i.Artist.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        CatalogGrid.ItemsSource = shown;
        NoMatch.Text = q == "" ? "Nothing in this section yet." : $"Nothing matches “{q}”.";
        NoMatch.Visibility = _items.Count > 0 && shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Cart ----------

    private void ToggleItem(CatalogItem item)
    {
        item.InCart = StoreCart.Toggle(item.ToLine());
        RefreshCart();
    }

    private void RemoveLine(string id)
    {
        StoreCart.Remove(id);
        var item = _items.FirstOrDefault(i => i.TitleId == id);
        if (item != null) item.InCart = false;
        RefreshCart();
    }

    private BitmapImage? PosterFor(string id) => _items.FirstOrDefault(i => i.TitleId == id)?.Poster;

    private void RefreshCart()
    {
        var lines = StoreCart.Lines;
        CartList.ItemsSource = lines.Select(l => new CartLine(l, PosterFor(l.Id), RemoveLine)).ToList();
        CartEmpty.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CartBadge.Value = lines.Count;
        CartBadge.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CartTotal.Text = $"{StoreCart.Total:N0} Nakfa";
        PlaceButton.IsEnabled = lines.Count > 0 && _base != null;
        PlaceText.Text = lines.Count == 0 ? "Place order" : $"Place order · {StoreCart.Total:N0} Nakfa";
    }

    private async void Place_Click(object sender, RoutedEventArgs e)
    {
        if (_base == null || StoreCart.Lines.Count == 0) return;
        PlaceButton.IsEnabled = false;
        PlaceText.Text = "Placing order…";
        CartError.IsOpen = false;
        try
        {
            var body = new JsonObject
            {
                ["device_public"] = PlayerState.DevicePublic,
                ["title_ids"] = new JsonArray(StoreCart.Lines.Select(l => (JsonNode)l.Id).ToArray()),
                ["replaces"] = StoreCart.Replaces,
            };
            // A fixed-length body: the tablet's small HTTP server can't read chunked uploads,
            // which is what PostAsJsonAsync would send.
            using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync(new Uri(_base, "api/orders"), content);
            if (!resp.IsSuccessStatusCode)
                throw new IOException((await resp.Content.ReadFromJsonAsync<JsonObject>())?["error"]?.GetValue<string>() ?? $"shop answered {(int)resp.StatusCode}");
            var o = (await resp.Content.ReadFromJsonAsync<JsonObject>())!;
            var pending = new StoreCart.Pending(_base.Authority, _shopName, o["code"]!.GetValue<string>(), o["token"]!.GetValue<string>(),
                o["total"]!.GetValue<long>(), StoreCart.Lines.ToList());
            StoreCart.Placed(pending);
            ShowOrder(pending);
        }
        catch (Exception ex)
        {
            CartError.Title = "The order didn't go through";
            CartError.Message = $"{ex.Message}. Stay on the shop's Wi-Fi and try again.";
            CartError.IsOpen = true;
        }
        finally { RefreshCart(); }
    }

    // ---------- Pickup code, payment and delivery ----------

    private void ShowOrder(StoreCart.Pending p)
    {
        OrderOverlay.Visibility = Visibility.Visible;
        OrderHeading.Text = "Show this code at the counter";
        CodeCard.Visibility = Visibility.Visible;
        DoneIcon.Visibility = Visibility.Collapsed;
        CodeText.Text = p.Code;
        OrderPay.Text = $"Pay {p.Total:N0} Nakfa in cash";
        OrderShop.Text = p.ShopName;
        OrderLinesCard.Visibility = Visibility.Visible;
        OrderLines.ItemsSource = p.Lines.Select(l => new CartLine(l, PosterFor(l.Id), null)).ToList();
        WaitingRow.Visibility = Visibility.Visible;
        OrderStatusText.Text = "Waiting for the cashier. This continues by itself once you've paid.";
        OrderProgress.Visibility = Visibility.Collapsed;
        OpenLibraryButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        BackToShopButton.Content = "Change my order";
        BackToShopButton.Visibility = Visibility.Visible;
        _poll?.Cancel();
        _poll = new CancellationTokenSource();
        _ = Follow(p, _poll.Token);
    }

    private async Task Follow(StoreCart.Pending p, CancellationToken stop)
    {
        var shop = ShopProtocol.BaseUri(p.Address);
        JsonObject? state = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                state = await Http.GetFromJsonAsync<JsonObject>(new Uri(shop, $"api/orders/{p.Token}"), stop);
                var s = state!["status"]!.GetValue<string>();
                if (s == "paid") break;
                if (s != "pending")
                {
                    OrderHeading.Text = $"Order {p.Code} was cancelled";
                    CodeCard.Visibility = Visibility.Collapsed;
                    OrderPay.Text = "Nothing was charged";
                    OrderStatusText.Text = "Your cart is still here if you want to order again.";
                    WaitingRow.Visibility = Visibility.Visible;
                    BackToShopButton.Content = "Back to the shop";
                    return;
                }
                OrderStatusText.Text = "Waiting for the cashier. This continues by itself once you've paid.";
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception)
            {
                OrderStatusText.Text = "Can't reach the shop right now. Stay on its Wi-Fi; this keeps trying.";
            }
            try { await Task.Delay(2000, stop); } catch (OperationCanceledException) { return; }
        }
        if (stop.IsCancellationRequested || state == null) return;

        // Paid: the licenses are signed for this PC. Save them first, then fetch any missing title files.
        OrderHeading.Text = "Paid. Thank you!";
        CodeCard.Visibility = Visibility.Collapsed;
        BackToShopButton.Visibility = Visibility.Collapsed;
        OrderProgress.Visibility = Visibility.Visible;
        var licenses = state["licenses"]!.AsArray();
        try
        {
            var n = 0;
            foreach (var lic in licenses)
            {
                var titleId = lic!["title_id"]!.GetValue<string>();
                PlayerState.ImportLicense(lic["bundle"]!.GetValue<string>());
                n++;
                if (!PlayerState.Library.Any(i => i.TitleId == titleId && File.Exists(i.Path)))
                {
                    var name = p.Lines.FirstOrDefault(l => l.Id == titleId)?.TitleEn ?? "title";
                    OrderPay.Text = $"Downloading {n} of {licenses.Count}";
                    OrderStatusText.Text = name;
                    var path = await Download(shop, titleId, stop);
                    await Task.Run(() => PlayerState.ImportTitle(path));
                }
            }
            StoreCart.Finished();
            var bought = licenses.Select(l => l!["title_id"]!.GetValue<string>()).ToHashSet();
            foreach (var i in _items) { i.InCart = false; if (bought.Contains(i.TitleId)) i.Owned = true; }
            RefreshCart();
            OrderHeading.Text = "Enjoy!";
            DoneIcon.Visibility = Visibility.Visible;
            OrderPay.Text = $"{licenses.Count} {(licenses.Count == 1 ? "title is" : "titles are")} in your library";
            OrderStatusText.Text = "They play on this PC only, without internet.";
            WaitingRow.Visibility = Visibility.Collapsed;
            OrderProgress.Visibility = Visibility.Collapsed;
            OpenLibraryButton.Visibility = Visibility.Visible;
            BackToShopButton.Content = "Keep shopping";
            BackToShopButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            OrderHeading.Text = "The download stopped";
            OrderPay.Text = "You've paid; your licenses are saved";
            OrderStatusText.Text = $"Stay on the shop's Wi-Fi and try again. Finished parts are kept. ({ex.Message})";
            OrderProgress.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (StoreCart.Order is { } p) ShowOrder(p);
    }

    private void BackToShop_Click(object sender, RoutedEventArgs e)
    {
        _poll?.Cancel();
        if (StoreCart.Order != null) StoreCart.ForgetOrder();
        OrderOverlay.Visibility = Visibility.Collapsed;
        RefreshCart();
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        OrderOverlay.Visibility = Visibility.Collapsed;
        App.Window?.ShowLibrary();
    }

    /// <summary>Download with resume: a dropped Wi-Fi connection continues where it stopped.</summary>
    private async Task<string> Download(Uri shop, string titleId, CancellationToken stop)
    {
        var dest = Path.Combine(PlayerState.TitlesDir, titleId + ".msb");
        var part = dest + ".part";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var have = File.Exists(part) ? new FileInfo(part).Length : 0;
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(shop, $"api/titles/{titleId}"));
                if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stop);
                resp.EnsureSuccessStatusCode();
                if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0;
                var total = have + (resp.Content.Headers.ContentLength ?? 0);
                await using var src = await resp.Content.ReadAsStreamAsync(stop);
                await using var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create);
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, stop)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), stop);
                    have += n;
                    if (total > 0) OrderProgress.Value = have * 100.0 / total;
                }
                dst.Close();
                File.Move(part, dest, overwrite: true);
                return dest;
            }
            catch (Exception) when (attempt < 4 && !stop.IsCancellationRequested)
            {
                await Task.Delay(1500, stop);
            }
        }
        throw new IOException("download failed");
    }
}
