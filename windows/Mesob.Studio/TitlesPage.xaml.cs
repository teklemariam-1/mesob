using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Mesob.Studio;

public sealed partial class TitlesPage : Page
{
    private string? _input;
    private string? _poster;
    private List<TitleRow> _all = [];

    public TitlesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private void Refresh()
    {
        _all = Studio.Db.Rows("SELECT t.*, (SELECT COUNT(*) FROM sales s WHERE s.title_id=t.title_id AND s.kind='sale') sold FROM titles t ORDER BY created DESC")
            .Select(t => new TitleRow
            {
                Id = (string)t["title_id"]!, TitleEn = (string)t["title_en"]!, TitleTi = t["title_ti"] as string ?? "", Artist = t["artist"] as string ?? "",
                Kind = (string)t["kind"]!, Size = Convert.ToInt64(t["size"]), Price = Convert.ToInt64(t["price"]), Sold = Convert.ToInt64(t["sold"]),
            }).ToList();
        Count.Text = $"{_all.Count} titles  ·  {Ui.Mb(_all.Sum(t => t.Size))} encrypted  ·  {_all.Sum(t => t.Sold):N0} licenses sold";
        Filter();
    }

    private void Filter()
    {
        var q = Search.Text.Trim();
        List.ItemsSource = q.Length == 0 ? _all : _all.Where(t =>
            t.TitleEn.Contains(q, StringComparison.OrdinalIgnoreCase) || t.TitleTi.Contains(q) || t.Artist.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Empty.Visibility = Ui.Show(_all.Count == 0);
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Filter();

    private void List_ItemClick(object sender, ItemClickEventArgs e) => PreviewWindow.Open((TitleRow)e.ClickedItem);

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        DialogError.Visibility = Visibility.Collapsed;
        AddDialog.XamlRoot = XamlRoot;
        if (await AddDialog.ShowAsync() != ContentDialogResult.Primary) return;

        var kind = (string)((ComboBoxItem)KindBox.SelectedItem).Tag;
        var (input, en, ti, artist, price, poster) = (_input!, TitleEnBox.Text.Trim(), TitleTiBox.Text.Trim(), ArtistBox.Text.Trim(), (long)PriceBox.Value, _poster);
        WorkingText.Text = $"Encrypting “{en}”…";
        Working.Visibility = Visibility.Visible;
        try
        {
            var id = await Task.Run(() => Studio.AddTitle(input, kind, en, ti, artist, price, poster));
            _input = null; _poster = null;
            PickedFile.Text = PickedPoster.Text = TitleEnBox.Text = TitleTiBox.Text = ArtistBox.Text = "";
            Refresh();
            await App.Message(XamlRoot, "Title added", $"“{en}” is encrypted (id {id}). It goes to shops on their next sync stick.");
        }
        catch (Exception ex) { await App.Message(XamlRoot, "Title not added", ex.Message); }
        finally { Working.Visibility = Visibility.Collapsed; }
    }

    private void AddDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var problem = _input == null ? "Choose a media file." : string.IsNullOrWhiteSpace(TitleEnBox.Text) ? "Enter the English title." : null;
        if (problem == null) return;
        DialogError.Text = problem;
        DialogError.Visibility = Visibility.Visible;
        args.Cancel = true;
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var picker = App.Picker(new FileOpenPicker());
        foreach (var ext in new[] { ".mp4", ".m4a", ".pdf" }) picker.FileTypeFilter.Add(ext);
        var f = await picker.PickSingleFileAsync();
        if (f == null) return;
        _input = f.Path;
        PickedFile.Text = f.Path;
        KindBox.SelectedIndex = Path.GetExtension(f.Path).ToLowerInvariant() switch { ".m4a" => 1, ".pdf" => 2, _ => 0 };
        if (string.IsNullOrWhiteSpace(TitleEnBox.Text)) TitleEnBox.Text = Path.GetFileNameWithoutExtension(f.Path);
    }

    private async void Poster_Click(object sender, RoutedEventArgs e)
    {
        var picker = App.Picker(new FileOpenPicker());
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png" }) picker.FileTypeFilter.Add(ext);
        var f = await picker.PickSingleFileAsync();
        if (f == null) return;
        if (new FileInfo(f.Path).Length > 500 * 1024)
        {
            DialogError.Text = "That poster is over 500 KB. Use a smaller image (for example 600×340 JPEG).";
            DialogError.Visibility = Visibility.Visible;
            return;
        }
        _poster = f.Path;
        PickedPoster.Text = f.Path;
    }
}
