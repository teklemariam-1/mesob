using Mesob.Interop;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Mesob.Studio;

/// <summary>
/// Plays a packaged title inside Studio Control so staff can check which file is which before
/// it goes to shops. The title is decrypted in memory with the studio's own key; nothing is
/// written to disk. Video and music play in a media player; books open page by page.
/// </summary>
public sealed class PreviewWindow : Window
{
    private static readonly Dictionary<string, PreviewWindow> OpenWindows = [];

    private readonly TitleRow _title;
    private DecryptingStream? _stream;
    private MediaPlayer? _player;
    private readonly MediaPlayerElement _mpe = new() { AreTransportControlsEnabled = true, Stretch = Stretch.Uniform };
    private PdfDocument? _doc;
    private uint _page;
    private readonly Image _pageImage = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ScrollViewer _pageScroll = new() { ZoomMode = ZoomMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _pageLabel = new() { Foreground = new SolidColorBrush(Colors.White), VerticalAlignment = VerticalAlignment.Center, MinWidth = 90, TextAlignment = TextAlignment.Center };
    private readonly Grid _body = new();

    /// <summary>Open (or bring forward) the preview for one title.</summary>
    public static void Open(TitleRow title)
    {
        if (OpenWindows.TryGetValue(title.Id, out var w)) { w.Activate(); return; }
        w = new PreviewWindow(title);
        OpenWindows[title.Id] = w;
        w.Activate();
    }

    private PreviewWindow(TitleRow title)
    {
        _title = title;
        Title = $"{title.TitleEn} · Preview · Mesob Studio Control";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "mesob.ico"));
        AppWindow.Resize(title.Kind == "audio" ? new Windows.Graphics.SizeInt32(760, 680) : new Windows.Graphics.SizeInt32(1120, 760));
        // Dark title bar to match the player.
        var bar = AppWindow.TitleBar;
        var dark = ColorHelper.FromArgb(255, 24, 24, 24);
        bar.BackgroundColor = bar.InactiveBackgroundColor = bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = dark;
        bar.ForegroundColor = bar.ButtonForegroundColor = Colors.White;
        bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 150, 150, 150);
        bar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 56, 56, 56);
        bar.ButtonHoverForegroundColor = Colors.White;

        var root = new Grid { Background = new SolidColorBrush(Colors.Black), RequestedTheme = ElementTheme.Dark };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Header());
        Grid.SetRow(_body, 1);
        root.Children.Add(_body);
        Content = root;
        Closed += (_, _) => Close_();
        root.Loaded += async (_, _) => await Start();
    }

    private Grid Header()
    {
        var bar = new Grid { Padding = new Thickness(20, 12, 20, 12), ColumnSpacing = 14, Background = new SolidColorBrush(ColorHelper.FromArgb(255, 24, 24, 24)) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thumb = new Grid { Width = 64, Height = 40, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 48, 48)) };
        thumb.Children.Add(new FontIcon { Glyph = _title.KindGlyph, FontSize = 15, Opacity = 0.6 });
        if (Ui.Poster(_title.Id) is { } poster) thumb.Children.Add(new Image { Source = poster, Stretch = Stretch.UniformToFill });
        bar.Children.Add(thumb);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        names.Children.Add(new TextBlock { Text = _title.TitleEn, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        names.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", new[] { _title.TitleTi, _title.Artist, _title.KindLabel, _title.PriceLabel }.Where(s => !string.IsNullOrEmpty(s))),
            FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = (FontFamily)Application.Current.Resources["EthiopicFont"],
        });
        Grid.SetColumn(names, 1);
        bar.Children.Add(names);

        var pill = new Border
        {
            Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(ColorHelper.FromArgb(60, 255, 255, 255)),
            Child = new TextBlock { Text = "STUDIO PREVIEW", FontSize = 11, FontWeight = FontWeights.SemiBold, CharacterSpacing = 80 },
        };
        ToolTipService.SetToolTip(pill, "Decrypted in memory with the studio key. Nothing is saved, and customers never get this view.");
        Grid.SetColumn(pill, 2);
        bar.Children.Add(pill);
        return bar;
    }

    private async Task Start()
    {
        try
        {
            _stream = await Task.Run(() => Studio.OpenPreview(_title.Id));
            if (_title.Kind == "pdf") await StartBook();
            else StartMedia();
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void StartMedia()
    {
        var mime = _stream!.Title["mime"]?.GetValue<string>() ?? (_title.Kind == "audio" ? "audio/mp4" : "video/mp4");
        var source = MediaSource.CreateFromStream(_stream.AsRandomAccessStream(), mime);
        _player = new MediaPlayer { Source = new MediaPlaybackItem(source), AutoPlay = true };
        _player.MediaFailed += (_, a) => DispatcherQueue.TryEnqueue(() => Fail(string.IsNullOrWhiteSpace(a.ErrorMessage)
            ? $"Windows can't play this {_title.KindLabel.ToLowerInvariant()}. The original file may not be a real {(_title.Kind == "audio" ? "M4A/MP4 audio" : "MP4 video")} file, or it uses a format this PC has no codec for. Check the original and add it again."
            : $"This title can't play on this PC: {a.ErrorMessage}"));
        _mpe.SetMediaPlayer(_player);
        _body.Children.Add(_mpe);
        if (_title.Kind != "audio") return;

        // Music: poster and names above the transport controls.
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 6, Margin = new Thickness(0, 0, 0, 120) };
        var art = new Grid { Width = 300, Height = 170, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(ColorHelper.FromArgb(255, 34, 34, 34)) };
        art.Children.Add(new FontIcon { Glyph = _title.KindGlyph, FontSize = 40, Opacity = 0.4 });
        if (Ui.Poster(_title.Id) is { } poster) art.Children.Add(new Image { Source = poster, Stretch = Stretch.UniformToFill });
        info.Children.Add(art);
        info.Children.Add(new TextBlock { Text = _title.TitleEn, FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
        if (_title.TitleTi.Length > 0)
            info.Children.Add(new TextBlock { Text = _title.TitleTi, FontSize = 18, Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = (FontFamily)Application.Current.Resources["EthiopicFont"] });
        if (_title.Artist.Length > 0)
            info.Children.Add(new TextBlock { Text = _title.Artist, FontSize = 14, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center });
        _body.Children.Add(info);
    }

    private async Task StartBook()
    {
        _doc = await PdfDocument.LoadFromStreamAsync(_stream!.AsRandomAccessStream());
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pageScroll.Content = _pageImage;
        _pageScroll.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 40, 40, 40));
        _pageImage.Margin = new Thickness(0, 20, 0, 20);
        layout.Children.Add(_pageScroll);

        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 10, Padding = new Thickness(12) };
        var prev = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        var next = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        ToolTipService.SetToolTip(prev, "Previous page");
        ToolTipService.SetToolTip(next, "Next page");
        prev.Click += async (_, _) => { if (_page > 0) await ShowPage(_page - 1); };
        next.Click += async (_, _) => await ShowPage(_page + 1);
        nav.Children.Add(prev);
        nav.Children.Add(_pageLabel);
        nav.Children.Add(next);
        Grid.SetRow(nav, 1);
        layout.Children.Add(nav);
        _body.Children.Add(layout);
        await ShowPage(0);
    }

    private async Task ShowPage(uint index)
    {
        if (_doc == null || index >= _doc.PageCount) return;
        _page = index;
        using var page = _doc.GetPage(index);
        using var mem = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(mem, new PdfPageRenderOptions { DestinationWidth = 1000 });
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(mem);
        _pageImage.Source = bmp;
        _pageImage.MaxWidth = 860;
        _pageLabel.Text = $"Page {index + 1} of {_doc.PageCount}";
        _pageScroll.ChangeView(null, 0, null, true);
    }

    private void Fail(string text)
    {
        _body.Children.Clear();
        var box = new StackPanel { Spacing = 12, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(new FontIcon { Glyph = "", FontSize = 34, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 242, 193, 116)) });
        box.Children.Add(new TextBlock { Text = text, FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        _body.Children.Add(box);
    }

    private void Close_()
    {
        OpenWindows.Remove(_title.Id);
        _player?.Pause();
        _mpe.SetMediaPlayer(null);
        _player?.Dispose();
        _pageImage.Source = null;
        _doc = null;
        _stream?.Dispose();
        _player = null;
        _stream = null;
    }
}
