using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mesob.Interop;
using Mesob.Studio;

// A full working day of the system with no network, run in a temp folder:
// vendor → studio → shop setup by stick → credits and codes → sales → sync back → reports → player.
var work = Path.Combine(Path.GetTempPath(), "mesob-systemtest-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
Environment.SetEnvironmentVariable("MESOB_STUDIO_DATA", Path.Combine(work, "studio"));
var stick = Path.Combine(work, "stick");
Directory.CreateDirectory(Path.Combine(stick, "MESOB"));
int passed = 0, failed = 0;

void Check(string what, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (ok) passed++; else failed++;
}

bool Throws(Action a, string? contains = null)
{
    try { a(); return false; }
    catch (Exception ex) { return contains == null || ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase); }
}

string B64(byte[] b) => Convert.ToBase64String(b);
byte[] Bytes(string b64) => Convert.FromBase64String(b64);
JsonNode Call(string op, JsonObject f) => MesobCore.Call(op, f);

Check("core is a lab build with the lab vendor key", MesobCore.IsLab && MesobCore.LabVendorKey != null);
var labRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\lab\vendor"));
var vendorSecret = B64(Convert.FromHexString(File.ReadAllText(Path.Combine(labRoot, "vendor.key")).Trim()));

// ---------- Vendor and studio ----------
Studio.Init();
Studio.CreateKey();
Check("studio key created and locked on this PC", Studio.HasKey);
Check("a second studio key is refused", Throws(Studio.CreateKey, "already has a key"));
var studioPub = B64(Convert.FromHexString(Studio.StudioPublicHex()));
var cert = Bytes(Call("vendor.studio_cert", new() { ["vendor_secret"] = vendorSecret, ["studio_id"] = 1, ["studio_public"] = studioPub, ["max_shops"] = 3 })["cert"]!.GetValue<string>());
Studio.ImportStudioCert(cert);
var cap = Bytes(Call("vendor.capacity", new() { ["vendor_secret"] = vendorSecret, ["block_id"] = 1, ["studio_id"] = 1, ["serial_start"] = 1_000_000, ["serial_count"] = 500 })["capacity"]!.GetValue<string>());
Studio.ImportCapacity(cap);
Check("studio ready: certificate + 500 capacity", Studio.IsReady && Studio.CapacityLeft() == 500);
var otherStudioCap = Bytes(Call("vendor.capacity", new() { ["vendor_secret"] = vendorSecret, ["block_id"] = 2, ["studio_id"] = 99, ["serial_start"] = 1, ["serial_count"] = 5 })["capacity"]!.GetValue<string>());
Check("capacity for another studio is refused", Throws(() => Studio.ImportCapacity(otherStudioCap), "another studio"));

// ---------- Titles ----------
var film = Path.Combine(work, "film.mp4");
var plain = new byte[3_300_000];
RandomNumberGenerator.Fill(plain);
File.WriteAllBytes(film, plain);
var titleId = Studio.AddTitle(film, "video", "Test Film", "ፈተና ፊልም", "Studio Artist", 150, null);
Check("title encrypted and added", titleId.Length == 32 && File.Exists(Path.Combine(Studio.TitlesDir, titleId + ".msb")));

// ---------- Shop tablet with hardware-held keys (stands in for Android Keystore) ----------
using var shopSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var shopKex = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
string Sec1(byte[] spki) => Call("key.spki_to_sec1", new() { ["spki"] = B64(spki) })["public"]!.GetValue<string>();
var signPub = Sec1(shopSign.ExportSubjectPublicKeyInfo());
var kexPub = Sec1(shopKex.ExportSubjectPublicKeyInfo());
string HwSign(string toSign) => B64(shopSign.SignData(Bytes(toSign), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
JsonObject HwOpen(string sealedB64)
{
    var eph = Bytes(Call("shop.sealed_eph", new() { ["sealed"] = sealedB64 })["eph_spki"]!.GetValue<string>());
    using var ephKey = ECDiffieHellman.Create();
    ephKey.ImportSubjectPublicKeyInfo(eph, out _);
    var shared = shopKex.DeriveRawSecretAgreement(ephKey.PublicKey);
    return new() { ["kex_shared"] = B64(shared), ["kex_public"] = kexPub };
}
JsonObject With(JsonObject a, JsonObject b) { foreach (var kv in b) a[kv.Key] = kv.Value?.DeepClone(); return a; }

File.WriteAllText(Path.Combine(stick, "MESOB", "setup-request.json"), new JsonObject { ["sign_public"] = signPub, ["kex_public"] = kexPub, ["model"] = "test tablet" }.ToJsonString());
var (shopId, setupFile) = Studio.SetUpShop(Path.Combine(stick, "MESOB", "setup-request.json"), "Test Shop", "Asmara", "07 000000");
Check("shop 1 set up from the tablet's request", shopId == 1 && File.Exists(setupFile));
Check("the same tablet can't be set up twice", Throws(() => Studio.SetUpShop(Path.Combine(stick, "MESOB", "setup-request.json"), "Again", "", ""), "already set up"));
var setup = JsonNode.Parse(File.ReadAllText(setupFile))!;
Call("shop.check_setup", new() { ["studio_cert"] = setup["studio_cert"]!.GetValue<string>(), ["shop_cert"] = setup["shop_cert"]!.GetValue<string>(), ["sign_public"] = signPub, ["kex_public"] = kexPub });
var sealedSecret = setup["sealed_secret"]!.GetValue<string>();
var codeSecret = Call("shop.open_secret", With(new() { ["sealed"] = sealedSecret, ["shop_id"] = shopId }, HwOpen(sealedSecret)))["secret"]!.GetValue<string>();
Check("tablet opened its code secret with its hardware key", codeSecret.Length > 0);
var shopCert = setup["shop_cert"]!.GetValue<string>();
var studioCert = setup["studio_cert"]!.GetValue<string>();

// ---------- Credits: locked pack, cash, unlock code ----------
var packId = Studio.CreatePack(shopId, 50, 60);
Check("credit pack made from capacity (450 left)", Studio.CapacityLeft() == 450);
Check("a pack bigger than capacity is refused", Throws(() => Studio.CreatePack(shopId, 10_000, 60), "not enough capacity"));
var code = Studio.Unlock(packId, 3000, "test payment");
var unlocked = Call("code.read_unlock", new() { ["secret"] = codeSecret, ["shop_id"] = shopId, ["code"] = code })["pack_id"]!.GetValue<long>();
Check($"unlock code {code} opens pack {packId} on the tablet", unlocked == packId);
Check("the code is refused by another shop", Throws(() => Call("code.read_unlock", new() { ["secret"] = codeSecret, ["shop_id"] = 2, ["code"] = code })));

// ---------- Sync stick: studio → shop ----------
Studio.WriteSync(stick, shopId);
var to = Path.Combine(stick, "MESOB", $"to-shop-{shopId}");
var catalog = File.ReadAllBytes(Path.Combine(to, "catalog.json"));
Call("catalog.verify", new() { ["studio_cert"] = studioCert, ["data"] = B64(catalog), ["sig"] = B64(File.ReadAllBytes(Path.Combine(to, "catalog.sig"))) });
var forged = (byte[])catalog.Clone();
forged[Array.IndexOf(forged, (byte)'1')] = (byte)'9';
Check("signed catalog verifies; an edited price is refused", Throws(() => Call("catalog.verify", new() { ["studio_cert"] = studioCert, ["data"] = B64(forged), ["sig"] = B64(File.ReadAllBytes(Path.Combine(to, "catalog.sig"))) })));
var capB64 = B64(File.ReadAllBytes(Path.Combine(to, "capacity", "1.bin")));
var packB64 = B64(File.ReadAllBytes(Path.Combine(to, "packs", $"{packId}.bin")));
var packInfo = Call("shop.check_pack", new() { ["studio_cert"] = studioCert, ["capacity"] = capB64, ["pack"] = packB64 });
Check("credit pack checks out against the vendor's capacity", packInfo["serial_count"]!.GetValue<long>() == 50);
var titleOnStick = Path.Combine(to, "titles", titleId + ".msb");
Call("title.inspect", new() { ["path"] = titleOnStick, ["check"] = true });
var sealedKey = B64(File.ReadAllBytes(Path.Combine(to, "keys", titleId + ".sealed")));

// ---------- Sales at the counter (hardware signing) ----------
var device = Call("device.create", new());
var log = new List<string>();
string? prev = null;
JsonNode Sell(long serial, JsonNode dev, string kind = "sale", long price = 150)
{
    var p = Call("shop.issue_prepare", With(new()
    {
        ["shop_cert"] = shopCert, ["pack"] = packB64, ["sealed_title_key"] = sealedKey, ["title_id"] = titleId,
        ["device_public"] = dev["public"]!.GetValue<string>(), ["serial"] = serial, ["reissue"] = kind == "reissue",
    }, HwOpen(sealedKey)));
    var bundle = Call("shop.issue_finish", new()
    {
        ["body"] = p["body"]!.GetValue<string>(), ["sig"] = HwSign(p["to_sign"]!.GetValue<string>()), ["shop_cert"] = shopCert,
        ["studio_cert"] = studioCert, ["pack"] = packB64, ["capacity"] = capB64, ["device_public"] = dev["public"]!.GetValue<string>(),
    });
    var lp = Call("log.prepare", new() { ["prev"] = prev, ["kind"] = kind, ["serial"] = serial, ["title_id"] = titleId, ["device"] = dev["fingerprint"]!.GetValue<string>(), ["price"] = price, ["local_time"] = 1_790_000_000 });
    var entry = Call("log.finish", new() { ["body"] = lp["body"]!.GetValue<string>(), ["sig"] = HwSign(lp["to_sign"]!.GetValue<string>()), ["shop_cert"] = shopCert });
    prev = entry["entry"]!.GetValue<string>();
    log.Add(prev);
    bundle.AsObject()["hash"] = entry["hash"]!.GetValue<string>();
    return bundle;
}
var sale = Sell(1_000_000, device);
Sell(1_000_001, Call("device.create", new()));
var lastHash = Sell(1_000_002, Call("device.create", new()))["hash"]!.GetValue<string>();
Check("three licenses issued and signed by the tablet's hardware key", log.Count == 3);
Check("a serial outside the pack is refused", Throws(() => Sell(1_000_050, device), "outside credit pack"));

// ---------- Player ----------
var bundleB64 = sale["bundle"]!.GetValue<string>();
using (var s = DecryptingStream.Open(titleOnStick, bundleB64, device["secret"]!.GetValue<string>()))
{
    var got = new byte[plain.Length];
    s.ReadExactly(got);
    s.Seek(2_000_000, SeekOrigin.Begin);
    var mid = new byte[4096];
    s.ReadExactly(mid);
    Check("buyer's player decrypts the title exactly (with seeking)", got.SequenceEqual(plain) && mid.SequenceEqual(plain.AsSpan(2_000_000, 4096).ToArray()));
}
var friend = Call("device.create", new());
Check("the same file + license is useless on a friend's device", Throws(() => DecryptingStream.Open(titleOnStick, bundleB64, friend["secret"]!.GetValue<string>()), "another device"));
var encrypted = File.ReadAllBytes(titleOnStick);
Check("the title file on the stick contains no plaintext", encrypted.AsSpan().IndexOf(plain.AsSpan(1_000_000, 64)) < 0);

// ---------- Report code and sync back: shop → studio ----------
var report = Call("code.report", new() { ["secret"] = codeSecret, ["shop_id"] = shopId, ["day"] = 310, ["count"] = 3, ["cash"] = 450, ["log_tag"] = lastHash[..6] })["code"]!.GetValue<string>();
var reportText = Studio.EnterReport(shopId, report);
Check($"daily report code accepted: {reportText}", reportText.Contains("3 sales"));
var from = Path.Combine(stick, "MESOB", $"from-shop-{shopId}");
Directory.CreateDirectory(from);
void WriteLog(IEnumerable<string> entries) => File.WriteAllText(Path.Combine(from, "log.json"), new JsonObject { ["shop_id"] = shopId, ["entries"] = new JsonArray(entries.Select(e => (JsonNode)e).ToArray()) }.ToJsonString());
WriteLog(log);
var results = Studio.ReadReturns(stick);
Check($"sales log imported: {results[0]}", Studio.Db.Long("SELECT COUNT(*) FROM sales") == 3);
Check("no alerts for an honest day", Studio.Db.Long("SELECT COUNT(*) FROM alerts") == 0);
Check("re-reading the same stick adds nothing", Studio.ReadReturns(stick)[0].Contains("0 new entries"));

// ---------- Fraud and tampering ----------
var tampered = log.ToList();
var raw = Bytes(tampered[1]);
raw[^70] ^= 0x01;
tampered[1] = B64(raw);
WriteLog(tampered);
Check("an edited sales log is rejected", Studio.ReadReturns(stick)[0].Contains("REJECTED"));
WriteLog(log.Take(2));
Check("a shortened sales log (deleted sale) is rejected", Studio.ReadReturns(stick)[0].Contains("REJECTED"));
Sell(1_000_001, Call("device.create", new()));   // a cloned/abused shop reuses a serial
WriteLog(log);
Studio.ReadReturns(stick);
Check("a serial sold twice raises an alert", Studio.Db.Long("SELECT COUNT(*) FROM alerts WHERE text LIKE '%sold 2 times%'") == 1);

// ---------- Re-issue to a new phone ----------
var newPhone = Call("device.create", new());
var replacement = Sell(1_000_000, newPhone, "reissue", 0);
using (var s = DecryptingStream.Open(titleOnStick, replacement["bundle"]!.GetValue<string>(), newPhone["secret"]!.GetValue<string>()))
    Check("replacement license (same serial) plays on the new phone", s.Length == plain.Length);

Console.WriteLine($"\n{passed} passed, {failed} failed");
try { Directory.Delete(work, true); } catch { /* SQLite may still hold the file briefly */ }
return failed == 0 ? 0 : 1;
