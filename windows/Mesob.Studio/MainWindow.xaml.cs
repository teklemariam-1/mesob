using Mesob.Interop;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Mesob.Studio;

public sealed partial class MainWindow : Window
{
    public string? StartupError { get; }

    public MainWindow()
    {
        InitializeComponent();
        Title = "Mesob Studio Control";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "mesob.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 920));
        try { Studio.Init(); }
        catch (Exception ex) { StartupError = ex.Message; }
        LabBadge.Visibility = MesobCore.IsLab ? Visibility.Visible : Visibility.Collapsed;
        RefreshBadge();
        Nav.SelectedItem = StartupError == null && Studio.IsReady ? Nav.MenuItems[0] : Nav.FooterMenuItems[0];
    }

    public void RefreshBadge()
    {
        if (StartupError != null || Studio.StudioCert == null) { StudioBadge.Visibility = Visibility.Collapsed; return; }
        StudioText.Text = $"Studio {Studio.StudioId}";
        StudioBadge.Visibility = Visibility.Visible;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = ((args.SelectedItem as NavigationViewItem)?.Tag as string) switch
        {
            "titles" => typeof(TitlesPage),
            "shops" => typeof(ShopsPage),
            "credits" => typeof(CreditsPage),
            "sync" => typeof(SyncPage),
            "reports" => typeof(ReportsPage),
            "setup" => typeof(SetupPage),
            _ => typeof(OverviewPage),
        };
        ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }
}
