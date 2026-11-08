using Mesob.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Mesob.Player;

/// <summary>Video and music. The decrypted stream goes straight into the Windows media pipeline.</summary>
public sealed partial class PlayerPage : Page
{
    private DecryptingStream? _stream;
    private MediaPlayer? _player;
    private DispatcherTimer? _timer;
    private readonly Random _rand = new();

    public PlayerPage() => InitializeComponent();

    private void Back_Click(object sender, RoutedEventArgs e) => App.Window?.GoBack();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var item = (LibraryItem)e.Parameter;
        TitleText.Text = item.TitleEn;
        SubText.Text = string.Join("  ·  ", new[] { item.TitleTi, item.Artist }.Where(s => !string.IsNullOrEmpty(s)));
        if (Protection.DebuggerAttached())
        {
            Fail("Playback is blocked while a debugger is attached.");
            return;
        }
        if (App.Window?.CaptureBlocked != true)
        {
            Fail("This PC can't block screen recording, so protected titles can't play here. Mesob needs Windows 10 version 2004 or newer.");
            return;
        }
        try
        {
            _stream = DecryptingStream.Open(item.Path, item.Bundle!, PlayerState.DeviceSecret);
            var source = MediaSource.CreateFromStream(_stream.AsRandomAccessStream(), item.Mime);
            _player = new MediaPlayer { Source = new MediaPlaybackItem(source), AutoPlay = true };
            _player.MediaFailed += (_, a) => DispatcherQueue.TryEnqueue(() => Fail($"This title can't play on this PC: {a.ErrorMessage}"));
            Mpe.SetMediaPlayer(_player);
            if (item.Kind == "audio")
            {
                AudioInfo.Visibility = Visibility.Visible;
                AudioPoster.Source = item.PosterImage;
                AudioTitle.Text = item.TitleEn;
                AudioTigrinya.Text = item.TitleTi;
                AudioArtist.Text = item.Artist;
            }
            Watermark.Text = Protection.WatermarkText(_stream.License["serial"]!.GetValue<ulong>(), PlayerState.DeviceFingerprint);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            _timer.Tick += (_, _) => MoveWatermark();
            _timer.Start();
            MoveWatermark();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void MoveWatermark()
    {
        var w = Math.Max(0, Overlay.ActualWidth - 260);
        var h = Math.Max(0, Overlay.ActualHeight - 160);
        Canvas.SetLeft(Watermark, 20 + _rand.NextDouble() * w);
        Canvas.SetTop(Watermark, 80 + _rand.NextDouble() * h);
    }

    private void Fail(string text)
    {
        ProblemText.Text = text;
        Problem.Visibility = Visibility.Visible;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer?.Stop();
        _player?.Pause();
        Mpe.SetMediaPlayer(null);
        _player?.Dispose();
        _stream?.Dispose();
        _player = null;
        _stream = null;
    }
}
