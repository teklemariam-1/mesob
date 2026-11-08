using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mesob.Interop;
using Mesob.Studio;

// Demo data for screenshots and trying the apps. Lab keys only.
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\demo"));
var assets = Path.Combine(root, "assets");
var studioDir = Path.Combine(root, "studio");
if (Directory.Exists(studioDir)) Directory.Delete(studioDir, true);
Environment.SetEnvironmentVariable("MESOB_STUDIO_DATA", studioDir);
var stick = Path.Combine(root, "stick");
if (Directory.Exists(stick)) Directory.Delete(stick, true);
Directory.CreateDirectory(Path.Combine(stick, "MESOB"));
var lab = Path.GetFullPath(Path.Combine(root, @"..\lab\vendor"));
string B64(byte[] b) => Convert.ToBase64String(b);
byte[] Bytes(string s) => Convert.FromBase64String(s);
JsonNode Call(string op, JsonObject f) => MesobCore.Call(op, f);
var vendorSecret = B64(Convert.FromHexString(File.ReadAllText(Path.Combine(lab, "vendor.key")).Trim()));

// ---------- Assets: posters for the made-up titles, and a small PDF book ----------
string Poster(string name, string line1, string line2, Color a, Color b)
{
    var path = Path.Combine(assets, name + ".jpg");
    using var bmp = new Bitmap(640, 360);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    using (var brush = new LinearGradientBrush(new Rectangle(0, 0, 640, 360), a, b, 35f)) g.FillRectangle(brush, 0, 0, 640, 360);
    using var big = new Font("Bahnschrift", 44, FontStyle.Bold);
    using var eth = new Font("Nyala", 34);
    g.DrawString(line1, big, Brushes.White, 28, 210);
    g.DrawString(line2, eth, new SolidBrush(Color.FromArgb(230, 255, 255, 255)), 30, 270);
    bmp.Save(path, ImageFormat.Jpeg);
    return path;
}
string Book(string path, string title, string[] pages)
{
    var objs = new List<string>();
    var kids = new List<string>();
    objs.Add("<< /Type /Catalog /Pages 2 0 R >>");
    objs.Add("PAGES");
    objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
    for (var i = 0; i < pages.Length; i++)
    {
        var lines = new StringBuilder($"BT /F1 26 Tf 60 760 Td ({title}) Tj ET\n");
        var y = 700;
        foreach (var l in pages[i].Split('\n')) { lines.Append($"BT /F1 14 Tf 60 {y} Td ({l}) Tj ET\n"); y -= 22; }
        lines.Append($"BT /F1 11 Tf 60 50 Td (Page {i + 1} of {pages.Length}) Tj ET\n");
        var content = lines.ToString();
        objs.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {objs.Count} 0 R >>");
        kids.Add($"{objs.Count} 0 R");
    }
    objs[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {kids.Count} >>";
    var sb = new StringBuilder("%PDF-1.4\n");
    var offsets = new List<int>();
    for (var i = 0; i < objs.Count; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
    var xref = sb.Length;
    sb.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
    foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
    sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
    return path;
}
string Filler(string name, int mb)
{
    var p = Path.Combine(assets, name + ".bin");
    if (!File.Exists(p)) { var b = new byte[mb << 20]; RandomNumberGenerator.Fill(b); File.WriteAllBytes(p, b); }
    return p;
}
var book = Book(Path.Combine(assets, "proverbs.pdf"), "Tigrinya Proverbs - a Mesob demo book", [
    "A short demo book, encrypted by Studio Control.\nOnly the device that bought it can open it.\n\nEvery page carries a faint watermark with the\nlicense serial and the device code.",
    "Copying this file to another phone or PC does not help:\nit stays locked until that device buys its own license\nat a Mesob shop.",
    "Screenshots and screen recording are blocked while\nthis book is open.",
]);

// ---------- Studio ----------
Studio.Init();
Studio.CreateKey();
var studioPub = B64(Convert.FromHexString(Studio.StudioPublicHex()));
Studio.ImportStudioCert(Bytes(Call("vendor.studio_cert", new() { ["vendor_secret"] = vendorSecret, ["studio_id"] = 7, ["studio_public"] = studioPub, ["max_shops"] = 20 })["cert"]!.GetValue<string>()));
Studio.ImportCapacity(Bytes(Call("vendor.capacity", new() { ["vendor_secret"] = vendorSecret, ["block_id"] = 1, ["studio_id"] = 7, ["serial_start"] = 1_000_000, ["serial_count"] = 50_000 })["capacity"]!.GetValue<string>()));
Console.WriteLine("Studio 7 ready with 50,000 capacity.");

var titles = new List<(string id, long price)>();
void Add(string file, string kind, string en, string ti, string artist, long price, string? poster)
{
    var id = Studio.AddTitle(file, kind, en, ti, artist, price, poster);
    titles.Add((id, price));
    Console.WriteLine($"  title {en} → {id}");
}
Add(@"C:\Users\tekke\Videos\HowThingsClick-Starlink\HowThingsClick-Starlink-Short-YouTube.mp4", "video", "How Things Click: Starlink", "ስታርሊንክ", "How Things Click", 150, Path.Combine(assets, "starlink.jpg"));
Add(@"C:\Users\tekke\Videos\HowThingsClick-SelfDriving-Final.mp4", "video", "How Things Click: Self-Driving Cars", "ብዘይ መራሕ ዝኸዳ መካይን", "How Things Click", 200, Path.Combine(assets, "selfdriving.jpg"));
Add(Path.Combine(assets, "starlink-audio.m4a"), "audio", "Starlink (audio)", "ስታርሊንክ", "How Things Click", 30, Path.Combine(assets, "starlink.jpg"));
Add(book, "pdf", "Tigrinya Proverbs", "ምስላታት ትግርኛ", "Mesob Books", 80, Poster("proverbs", "Proverbs", "ምስላታት ትግርኛ", Color.FromArgb(122, 90, 0), Color.FromArgb(40, 30, 10)));
Add(Filler("asmara-nights", 3), "video", "Asmara Nights", "ለይቲ ኣስመራ", "Studio Seven", 180, Poster("asmara", "Asmara Nights", "ለይቲ ኣስመራ", Color.FromArgb(20, 40, 90), Color.FromArgb(200, 80, 40)));
Add(Filler("hade-libi", 1), "audio", "Hade Libi", "ሓደ ልቢ", "Studio Seven Band", 40, Poster("hadelibi", "Hade Libi", "ሓደ ልቢ", Color.FromArgb(90, 20, 70), Color.FromArgb(20, 20, 40)));

// ---------- Shops (simulated tablets with hardware-held keys) ----------
var shops = new List<(long id, ECDsa sign, ECDiffieHellman kex, string kexPub, string cert, string secret)>();
string Sec1(byte[] spki) => Call("key.spki_to_sec1", new() { ["spki"] = B64(spki) })["public"]!.GetValue<string>();
foreach (var (name, town, phone) in new[] { ("Harnet Avenue Media", "Asmara", "07 123 456"), ("Keren Video Centre", "Keren", "07 234 567"), ("Mendefera Electronics", "Mendefera", "07 345 678") })
{
    var sign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var kex = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    var kexPub = Sec1(kex.ExportSubjectPublicKeyInfo());
    var req = Path.Combine(stick, "MESOB", "setup-request.json");
    File.WriteAllText(req, new JsonObject { ["sign_public"] = Sec1(sign.ExportSubjectPublicKeyInfo()), ["kex_public"] = kexPub, ["model"] = "Samsung Galaxy Tab A9+", ["android"] = 34, ["kex_hardware"] = true }.ToJsonString());
    var (id, file) = Studio.SetUpShop(req, name, town, phone);
    var setup = JsonNode.Parse(File.ReadAllText(file))!;
    var sealedSecret = setup["sealed_secret"]!.GetValue<string>();
    shops.Add((id, sign, kex, kexPub, setup["shop_cert"]!.GetValue<string>(), Open(kex, kexPub, sealedSecret, o => Call("shop.open_secret", With(new() { ["sealed"] = sealedSecret, ["shop_id"] = id }, o))["secret"]!.GetValue<string>())));
    Console.WriteLine($"Shop {id}: {name}, {town}");
}
JsonObject With(JsonObject a, JsonObject b) { foreach (var kv in b) a[kv.Key] = kv.Value?.DeepClone(); return a; }
string Open(ECDiffieHellman kex, string kexPub, string sealedB64, Func<JsonObject, string> f)
{
    var eph = Bytes(Call("shop.sealed_eph", new() { ["sealed"] = sealedB64 })["eph_spki"]!.GetValue<string>());
    using var e = ECDiffieHellman.Create();
    e.ImportSubjectPublicKeyInfo(eph, out _);
    return f(new() { ["kex_shared"] = B64(kex.DeriveRawSecretAgreement(e.PublicKey)), ["kex_public"] = kexPub });
}

// Credit packs: most paid and unlocked, one waiting for payment.
var packs = new Dictionary<long, long>();
foreach (var (sid, size, wholesale, paid) in new[] { (1L, 500L, 60L, true), (1L, 500L, 60L, false), (2L, 300L, 60L, true), (3L, 200L, 65L, true) })
{
    var pid = Studio.CreatePack(sid, size, wholesale);
    if (paid) { Studio.Unlock(pid, size * wholesale, $"Cash at studio, receipt R-{1000 + pid}"); packs.TryAdd(sid, pid); }
}
Studio.WriteSync(stick, 1);
Studio.WriteSync(stick, 2);
Studio.WriteSync(stick, 3);

// ---------- A week of sales ----------
var rand = new Random(7);
var start = DateTimeOffset.Now.AddDays(-6).Date;
string capB64 = B64(File.ReadAllBytes(Path.Combine(stick, "MESOB", "to-shop-1", "capacity", "1.bin")));
string studioCert = Studio.StudioCert!;
var shopLogs = new Dictionary<long, List<string>>();
var nextSerial = new Dictionary<long, long>();
JsonNode Sell(int shopIdx, string titleId, long price, JsonNode device, DateTimeOffset when, long? serialOverride = null, string kind = "sale")
{
    var s = shops[shopIdx];
    var pid = packs[s.id];
    var dir = Path.Combine(stick, "MESOB", $"to-shop-{s.id}");
    var packB64 = B64(File.ReadAllBytes(Path.Combine(dir, "packs", $"{pid}.bin")));
    var sealedKey = B64(File.ReadAllBytes(Path.Combine(dir, "keys", titleId + ".sealed")));
    var packStart = Call("record.decode", new() { ["record"] = packB64 })["serial_start"]!.GetValue<long>();
    var serial = serialOverride ?? (nextSerial.TryGetValue(s.id, out var n) ? n : packStart);
    if (serialOverride == null) nextSerial[s.id] = serial + 1;
    var p = Open(s.kex, s.kexPub, sealedKey, o => Call("shop.issue_prepare", With(new()
    {
        ["shop_cert"] = s.cert, ["pack"] = packB64, ["sealed_title_key"] = sealedKey, ["title_id"] = titleId,
        ["device_public"] = device["public"]!.GetValue<string>(), ["serial"] = serial, ["reissue"] = kind == "reissue",
    }, o)).ToJsonString());
    var pj = JsonNode.Parse(p)!;
    string HwSign(string m) => B64(s.sign.SignData(Bytes(m), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    var bundle = Call("shop.issue_finish", new()
    {
        ["body"] = pj["body"]!.GetValue<string>(), ["sig"] = HwSign(pj["to_sign"]!.GetValue<string>()), ["shop_cert"] = s.cert,
        ["studio_cert"] = studioCert, ["pack"] = packB64, ["capacity"] = capB64, ["device_public"] = device["public"]!.GetValue<string>(),
    });
    var log = shopLogs.TryGetValue(s.id, out var l) ? l : shopLogs[s.id] = [];
    var lp = Call("log.prepare", new() { ["prev"] = log.LastOrDefault(), ["kind"] = kind, ["serial"] = serial, ["title_id"] = titleId, ["device"] = device["fingerprint"]!.GetValue<string>(), ["price"] = price, ["local_time"] = when.ToUnixTimeSeconds() });
    var entry = Call("log.finish", new() { ["body"] = lp["body"]!.GetValue<string>(), ["sig"] = HwSign(lp["to_sign"]!.GetValue<string>()), ["shop_cert"] = s.cert });
    log.Add(entry["entry"]!.GetValue<string>());
    bundle.AsObject()["hash"] = entry["hash"]!.GetValue<string>();
    return bundle;
}
var weights = new[] { 5, 3, 4, 2, 6, 3 };
for (var day = 0; day < 7; day++)
    for (var si = 0; si < shops.Count; si++)
        for (var k = 0; k < rand.Next(3, 9) - si; k++)
        {
            var pick = rand.Next(weights.Sum());
            var ti = 0;
            while (pick >= weights[ti]) pick -= weights[ti++];
            Sell(si, titles[ti].id, titles[ti].price, Call("device.create", new()), new DateTimeOffset(start.AddDays(day).AddHours(9 + rand.Next(10))));
        }

// ---------- This PC's Player: buy three titles from shop 1 ----------
var playerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mesob", "Player");
var store = new SecureStore(Path.Combine(playerDir, "keys"), "Player");
var devSecret = store.Load("device") ?? Bytes(Call("device.create", new())["secret"]!.GetValue<string>()).Also(b => store.Save("device", b));
var dev = Call("device.info", new() { ["secret"] = B64(devSecret) });
dev.AsObject()["secret"] = B64(devSecret);
var library = new JsonArray();
var videosDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Mesob");
Directory.CreateDirectory(videosDir);
Directory.CreateDirectory(Path.Combine(playerDir, "posters"));
string? lastHash1 = null;
foreach (var (i, buy) in new[] { (0, true), (2, true), (3, true), (1, false), (4, false), (5, false) })
{
    var (id, price) = titles[i];
    var src = Path.Combine(Studio.TitlesDir, id + ".msb");
    var dst = Path.Combine(videosDir, id + ".msb");
    File.Copy(src, dst, true);
    var h = Call("title.inspect", new() { ["path"] = dst });
    var poster = Bytes(h["poster"]!.GetValue<string>());
    string? posterFile = null;
    if (poster.Length > 0) { posterFile = Path.Combine(playerDir, "posters", id + ".img"); File.WriteAllBytes(posterFile, poster); }
    string? bundle = null;
    long serial = 0;
    if (buy)
    {
        var b = Sell(0, id, price, dev, DateTimeOffset.Now.AddMinutes(-30 + i));
        bundle = b["bundle"]!.GetValue<string>();
        lastHash1 = b["hash"]!.GetValue<string>();
        serial = Call("license.verify", new() { ["bundle"] = bundle, ["device_secret"] = dev["secret"]!.GetValue<string>() })["serial"]!.GetValue<long>();
    }
    library.Add(new JsonObject
    {
        ["Path"] = dst, ["TitleId"] = id, ["TitleEn"] = h["title_en"]!.GetValue<string>(), ["TitleTi"] = h["title_ti"]!.GetValue<string>(),
        ["Artist"] = h["artist"]!.GetValue<string>(), ["Kind"] = h["kind"]!.GetValue<string>(), ["Mime"] = h["mime"]!.GetValue<string>(),
        ["Size"] = h["size"]!.GetValue<long>(), ["Bundle"] = bundle, ["Serial"] = serial, ["PosterFile"] = posterFile,
    });
}
File.WriteAllText(Path.Combine(playerDir, "library.json"), library.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(Path.Combine(playerDir, "pending-licenses.json"), "[]");
Console.WriteLine($"Player: 3 titles licensed to this PC ({dev["fingerprint"]}), 3 locked.");

// One copied tablet reuses a serial at shop 3 (shows the fraud alert).
var dupSerial = Call("record.decode", new() { ["record"] = B64(File.ReadAllBytes(Path.Combine(stick, "MESOB", "to-shop-3", "packs", $"{packs[3]}.bin"))) })["serial_start"]!.GetValue<long>();
Sell(2, titles[4].id, 180, Call("device.create", new()), DateTimeOffset.Now.AddHours(-2), dupSerial);

// ---------- Logs back to the studio, and today's report codes ----------
foreach (var s in shops)
{
    var from = Path.Combine(stick, "MESOB", $"from-shop-{s.id}");
    Directory.CreateDirectory(from);
    File.WriteAllText(Path.Combine(from, "log.json"), new JsonObject { ["shop_id"] = s.id, ["entries"] = new JsonArray(shopLogs[s.id].Select(e => (JsonNode)e).ToArray()) }.ToJsonString());
}
foreach (var line in Studio.ReadReturns(stick)) Console.WriteLine(line);
var v = Call("log.verify", new() { ["shop_cert"] = shops[0].cert, ["entries"] = new JsonArray(shopLogs[1].Select(e => (JsonNode)e).ToArray()) });
var todays = v["detail"]!.AsArray().Where(d => DateTimeOffset.FromUnixTimeSeconds(d!["local_time"]!.GetValue<long>()).LocalDateTime.Date == DateTime.Today && d["kind"]!.GetValue<string>() == "sale").ToList();
var code = Call("code.report", new() { ["secret"] = shops[0].secret, ["shop_id"] = 1, ["day"] = 310, ["count"] = todays.Count, ["cash"] = todays.Sum(d => d!["price"]!.GetValue<long>()), ["log_tag"] = v["last_hash"]!.GetValue<string>()[..6] })["code"]!.GetValue<string>();
Console.WriteLine(Studio.EnterReport(1, code));
Console.WriteLine($"\nDemo ready. Studio data: {studioDir}");

static class Ext
{
    public static T Also<T>(this T v, Action<T> a) { a(v); return v; }
}
