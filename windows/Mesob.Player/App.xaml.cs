using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mesob.Player;

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
        var d = new ContentDialog { Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK", XamlRoot = root };
        await d.ShowAsync();
    }

    /// <summary>File pickers in an unpackaged app need the window handle.</summary>
    public static T Picker<T>(T picker) where T : class
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        return picker;
    }
}
