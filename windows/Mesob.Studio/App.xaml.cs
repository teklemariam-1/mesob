using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mesob.Studio;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }
    public static IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
    }

    public static async Task Message(XamlRoot root, string title, string text)
    {
        var d = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "OK",
            XamlRoot = root,
        };
        await d.ShowAsync();
    }

    public static async Task<bool> Confirm(XamlRoot root, string title, string text, string yes)
    {
        var d = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = yes,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Show a credit unlock code large enough to read out over the phone, with a copy button.</summary>
    public static async Task ShowCode(XamlRoot root, string title, string code, string note)
    {
        var copy = new Button { Content = "Copy code" };
        copy.Click += (_, _) =>
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(code);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            copy.Content = "Copied";
        };
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 16, 20, 16),
            Child = new TextBlock
            {
                Text = code,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                IsTextSelectionEnabled = true,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        });
        panel.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        panel.Children.Add(copy);
        await new ContentDialog { Title = title, Content = panel, CloseButtonText = "Done", XamlRoot = root }.ShowAsync();
    }

    public static T Picker<T>(T picker) where T : class
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        return picker;
    }
}
