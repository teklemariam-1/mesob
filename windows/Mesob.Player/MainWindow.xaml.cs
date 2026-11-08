using Mesob.Interop;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Mesob.Player;

public sealed partial class MainWindow : Window
{
    public bool CaptureBlocked { get; }
    public string? StartupError { get; }

    public MainWindow()
    {
        InitializeComponent();
        Title = "Mesob Player";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "mesob.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 860));

        // The whole window is left out of screenshots, recordings and screen sharing.
        // Lab builds only: MESOB_LAB_SCREENSHOTS=1 leaves it capturable for documentation screenshots.
        // A production core reports IsLab = false, so shipped apps always block capture.
        var labScreenshots = MesobCore.IsLab && Environment.GetEnvironmentVariable("MESOB_LAB_SCREENSHOTS") == "1";
        CaptureBlocked = labScreenshots || Protection.ExcludeFromCapture(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (MesobCore.IsLab)
        {
            LabBadge.Visibility = Visibility.Visible;
            LabText.Text = labScreenshots ? "LAB BUILD · SCREENSHOT MODE" : "LAB BUILD";
        }
        try { PlayerState.Init(); }
        catch (Exception ex) { StartupError = ex.Message; }
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = ((args.SelectedItem as NavigationViewItem)?.Tag as string) switch
        {
            "shop" => typeof(ShopPage),
            "device" => typeof(DevicePage),
            _ => typeof(LibraryPage),
        };
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => GoBack();

    public void ShowLibrary() => Nav.SelectedItem = Nav.MenuItems[0];

    public void GoBack()
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        Nav.IsBackEnabled = ContentFrame.CanGoBack;
        // Playback is full-bleed: hide the side pane while a title is open.
        var playing = e.SourcePageType == typeof(PlayerPage) || e.SourcePageType == typeof(ReaderPage);
        Nav.IsPaneVisible = !playing;
        if (!playing)
        {
            var tag = e.SourcePageType == typeof(ShopPage) ? "shop" : e.SourcePageType == typeof(DevicePage) ? "device" : "library";
            var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
            if (item != null && Nav.SelectedItem != item) Nav.SelectedItem = item;
        }
    }

    public void Open(LibraryItem item) =>
        ContentFrame.Navigate(item.Kind == "pdf" ? typeof(ReaderPage) : typeof(PlayerPage), item, new DrillInNavigationTransitionInfo());
}
