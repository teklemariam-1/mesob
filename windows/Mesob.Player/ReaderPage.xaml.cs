using Mesob.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Mesob.Player;

/// <summary>PDF books: pages are rendered from the decrypted stream into memory only. No export, no print.</summary>
public sealed partial class ReaderPage : Page
{
    private DecryptingStream? _stream;
    private PdfDocument? _doc;
    private uint _page;
    private readonly Random _rand = new();

    public ReaderPage() => InitializeComponent();

    private void Back_Click(object sender, RoutedEventArgs e) => App.Window?.GoBack();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var item = (LibraryItem)e.Parameter;
        BookTitle.Text = item.TitleEn;
        BookSub.Text = string.Join("  ·  ", new[] { item.TitleTi, item.Artist }.Where(s => !string.IsNullOrEmpty(s)));
        if (Protection.DebuggerAttached() || App.Window?.CaptureBlocked != true)
        {
            Fail("This book can't be opened here: screen capture can't be blocked or a debugger is attached.");
            return;
        }
        try
        {
            _stream = DecryptingStream.Open(item.Path, item.Bundle!, PlayerState.DeviceSecret);
            _doc = await PdfDocument.LoadFromStreamAsync(_stream.AsRandomAccessStream());
            Watermark.Text = Protection.WatermarkText(_stream.License["serial"]!.GetValue<ulong>(), PlayerState.DeviceFingerprint);
            await Show(0);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private async Task Show(uint index)
    {
        if (_doc == null || index >= _doc.PageCount) return;
        _page = index;
        using var page = _doc.GetPage(index);
        using var mem = new InMemoryRandomAccessStream();
        var width = (uint)Math.Clamp(ActualWidth * 0.62, 640, 1200);
        await page.RenderToStreamAsync(mem, new PdfPageRenderOptions { DestinationWidth = width });
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(mem);
        PageImage.Source = bmp;
        PageLabel.Text = $"{index + 1} of {_doc.PageCount}";
        Scroller.ChangeView(null, 0, null, true);
        Canvas.SetLeft(Watermark, 60 + _rand.NextDouble() * Math.Max(0, Overlay.ActualWidth - 320));
        Canvas.SetTop(Watermark, 60 + _rand.NextDouble() * Math.Max(0, Overlay.ActualHeight - 140));
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) { if (_page > 0) await Show(_page - 1); }
    private async void Next_Click(object sender, RoutedEventArgs e) => await Show(_page + 1);

    private void Fail(string text)
    {
        ProblemText.Text = text;
        Problem.Visibility = Visibility.Visible;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        PageImage.Source = null;
        _doc = null;
        _stream?.Dispose();
        _stream = null;
    }
}
