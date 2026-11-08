using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Mesob.Interop;

/// <summary>An operation the Rust core refused, with its reason.</summary>
public sealed class MesobException(string message) : Exception(message);

/// <summary>
/// Calls into the Rust core (mesob.dll). Every operation is one JSON request; see
/// core/mesob-ffi/src/ops.rs for the list. Playback reads go through <see cref="DecryptingStream"/>.
/// </summary>
public static class MesobCore
{
    private const string Dll = "mesob.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mesob_call(byte[] requestUtf8);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mesob_free(IntPtr s);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe long mesob_session_read(ulong handle, ulong offset, byte* buf, nuint len);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long mesob_session_size(ulong handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mesob_session_close(ulong handle);

    private static string? _labVendor;
    private static bool? _isLab;

    /// <summary>True for development builds of the core (no vendor key compiled in).</summary>
    public static bool IsLab => _isLab ??= RawCall(new JsonObject { ["op"] = "core.info" })["lab"]!.GetValue<bool>();

    /// <summary>
    /// Lab builds only: the vendor public key (hex), read from vendor.pub next to the app or in
    /// %LOCALAPPDATA%\Mesob\lab. Production cores ignore this; their key is compiled in.
    /// </summary>
    public static string? LabVendorKey
    {
        get
        {
            if (_labVendor != null || !IsLab) return _labVendor;
            foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mesob", "lab") })
            {
                var p = Path.Combine(dir, "vendor.pub");
                if (File.Exists(p)) return _labVendor = File.ReadAllText(p).Trim();
            }
            return null;
        }
    }

    /// <summary>Run one operation. Throws <see cref="MesobException"/> with the core's reason on failure.</summary>
    public static JsonNode Call(string op, JsonObject? fields = null)
    {
        var req = fields ?? new JsonObject();
        req["op"] = op;
        if (IsLab && LabVendorKey is { } v && !req.ContainsKey("vendor_pub")) req["vendor_pub"] = v;
        return RawCall(req);
    }

    private static JsonNode RawCall(JsonObject req)
    {
        var bytes = Encoding.UTF8.GetBytes(req.ToJsonString() + "\0");
        var ptr = mesob_call(bytes);
        string json;
        try { json = Marshal.PtrToStringUTF8(ptr) ?? "{}"; }
        finally { mesob_free(ptr); }
        var resp = JsonNode.Parse(json)!;
        if (resp["ok"]?.GetValue<bool>() != true)
            throw new MesobException(resp["error"]?.GetValue<string>() ?? "unknown error");
        return resp["result"]!;
    }
}
