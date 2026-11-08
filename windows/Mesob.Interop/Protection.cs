using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mesob.Interop;

/// <summary>Window and process protections used while protected content is on screen.</summary>
public static class Protection
{
    private const uint WDA_MONITOR = 0x1;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("kernel32.dll")]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr process, ref bool present);

    /// <summary>
    /// Hide the window from screenshots, screen recording and screen sharing. On Windows 10 2004+
    /// the window is left out of captures; on older builds it shows black. Returns false if
    /// neither is available.
    /// </summary>
    public static bool ExcludeFromCapture(IntPtr hwnd) =>
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) || SetWindowDisplayAffinity(hwnd, WDA_MONITOR);

    /// <summary>True when a debugger is attached; protected playback refuses to start.</summary>
    public static bool DebuggerAttached()
    {
        if (Debugger.IsAttached || IsDebuggerPresent()) return true;
        var remote = false;
        CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref remote);
        return remote;
    }

    /// <summary>Short watermark text: license serial plus the start of the device id.</summary>
    public static string WatermarkText(ulong serial, string deviceHex) =>
        $"{serial} · {deviceHex[..Math.Min(8, deviceHex.Length)].ToUpperInvariant()}";
}
