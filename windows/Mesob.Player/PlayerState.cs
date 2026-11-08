using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mesob.Interop;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mesob.Player;

/// <summary>A title in the library: the encrypted file plus (once bought) its license bundle.</summary>
public sealed class LibraryItem
{
    public string Path { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public string TitleTi { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Kind { get; set; } = "video";
    public string Mime { get; set; } = "video/mp4";
    public long Size { get; set; }
    public string? Bundle { get; set; }
    public ulong Serial { get; set; }
    public string? PosterFile { get; set; }

    [JsonIgnore] public bool Locked => Bundle == null;
    [JsonIgnore] public string StateLabel => Locked ? "Locked" : "Yours";
    [JsonIgnore] public string KindLabel => Kind switch { "audio" => "MUSIC", "pdf" => "BOOK", _ => "VIDEO" };
    [JsonIgnore] public string KindGlyph => Kind switch { "audio" => "", "pdf" => "", _ => "" };
    [JsonIgnore] public string SizeLabel => Size >= 1048576 ? $"{Size / 1048576.0:0.#} MB" : $"{Math.Max(1, Size / 1024)} KB";
    [JsonIgnore] public string Byline => string.IsNullOrEmpty(Artist) ? SizeLabel : $"{Artist} · {SizeLabel}";
    [JsonIgnore] public Microsoft.UI.Xaml.Visibility LockedVisibility => Locked ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    [JsonIgnore] public Microsoft.UI.Xaml.Visibility OwnedVisibility => Locked ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    [JsonIgnore] public Microsoft.UI.Xaml.Visibility NoPosterVisibility => PosterFile != null && File.Exists(PosterFile) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    [JsonIgnore]
    public ImageSource? PosterImage => PosterFile != null && File.Exists(PosterFile) ? new BitmapImage(new Uri(PosterFile)) : null;
}

/// <summary>Device key, library and where files live.</summary>
public static class PlayerState
{
    public static string DataDir { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mesob", "Player");
    /// <summary>Encrypted titles live in a visible folder so they can be shared; they're useless without a license.</summary>
    public static string TitlesDir { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Mesob");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string LibraryFile => System.IO.Path.Combine(DataDir, "library.json");
    private static string PendingFile => System.IO.Path.Combine(DataDir, "pending-licenses.json");

    public static SecureStore Store { get; private set; } = null!;
    public static string DeviceSecret { get; private set; } = "";
    public static string DevicePublic { get; private set; } = "";
    public static string DeviceFingerprint { get; private set; } = "";
    public static List<LibraryItem> Library { get; private set; } = [];
    private static List<string> _pending = [];

    public static void Init()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(TitlesDir);
        Directory.CreateDirectory(System.IO.Path.Combine(DataDir, "posters"));
        Store = new SecureStore(System.IO.Path.Combine(DataDir, "keys"), "Player");
        var secret = Store.Load("device");
        if (secret == null)
        {
            var created = MesobCore.Call("device.create");
            secret = Convert.FromBase64String(created["secret"]!.GetValue<string>());
            Store.Save("device", secret);
        }
        DeviceSecret = Convert.ToBase64String(secret);
        Array.Clear(secret);
        var info = MesobCore.Call("device.info", new JsonObject { ["secret"] = DeviceSecret });
        DevicePublic = info["public"]!.GetValue<string>();
        DeviceFingerprint = info["fingerprint"]!.GetValue<string>();
        if (File.Exists(LibraryFile)) Library = JsonSerializer.Deserialize<List<LibraryItem>>(File.ReadAllText(LibraryFile)) ?? [];
        if (File.Exists(PendingFile)) _pending = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PendingFile)) ?? [];
        Library.RemoveAll(i => !File.Exists(i.Path));
    }

    public static void Save()
    {
        File.WriteAllText(LibraryFile, JsonSerializer.Serialize(Library, Json));
        File.WriteAllText(PendingFile, JsonSerializer.Serialize(_pending, Json));
    }

    /// <summary>Check a title file's signatures and fingerprints, keep it in the library folder and add it.</summary>
    public static LibraryItem ImportTitle(string path)
    {
        var h = MesobCore.Call("title.inspect", new JsonObject { ["path"] = path, ["check"] = true });
        var id = h["title_id"]!.GetValue<string>();
        var dest = System.IO.Path.Combine(TitlesDir, id + ".msb");
        if (!string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            File.Copy(path, dest, overwrite: true);
        var item = Library.FirstOrDefault(i => i.TitleId == id) ?? new LibraryItem();
        item.Path = dest;
        item.TitleId = id;
        item.TitleEn = h["title_en"]!.GetValue<string>();
        item.TitleTi = h["title_ti"]!.GetValue<string>();
        item.Artist = h["artist"]!.GetValue<string>();
        item.Kind = h["kind"]!.GetValue<string>();
        item.Mime = h["mime"]!.GetValue<string>();
        item.Size = h["size"]!.GetValue<long>();
        var poster = Convert.FromBase64String(h["poster"]!.GetValue<string>());
        if (poster.Length > 0)
        {
            item.PosterFile = System.IO.Path.Combine(DataDir, "posters", id + ".img");
            File.WriteAllBytes(item.PosterFile, poster);
        }
        if (!Library.Contains(item)) Library.Add(item);
        foreach (var b in _pending.ToList())
            if (TryAttach(b)) _pending.Remove(b);
        Save();
        return item;
    }

    /// <summary>Check a license for this device. Attaches it to its title, or keeps it until the title arrives.</summary>
    public static string ImportLicense(string bundleB64)
    {
        var v = MesobCore.Call("license.verify", new JsonObject { ["bundle"] = bundleB64, ["device_secret"] = DeviceSecret });
        if (!TryAttach(bundleB64) && !_pending.Contains(bundleB64)) _pending.Add(bundleB64);
        Save();
        return v["title_id"]!.GetValue<string>();
    }

    private static bool TryAttach(string bundleB64)
    {
        var v = MesobCore.Call("license.verify", new JsonObject { ["bundle"] = bundleB64, ["device_secret"] = DeviceSecret });
        var item = Library.FirstOrDefault(i => i.TitleId == v["title_id"]!.GetValue<string>());
        if (item == null) return false;
        item.Bundle = bundleB64;
        item.Serial = v["serial"]!.GetValue<ulong>();
        return true;
    }

    /// <summary>A .msl file holds one bundle; a .msls file holds several (u16 length + bytes each).</summary>
    public static IEnumerable<string> ReadLicenseFile(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 'M' && data[1] == 'S' && data[2] == 'L' && data[3] == '1')
        {
            yield return Convert.ToBase64String(data);
            yield break;
        }
        var i = 0;
        while (i + 2 <= data.Length)
        {
            var n = (data[i] << 8) | data[i + 1];
            yield return Convert.ToBase64String(data, i + 2, n);
            i += 2 + n;
        }
    }
}
