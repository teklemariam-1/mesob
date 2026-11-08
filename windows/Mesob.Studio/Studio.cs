using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mesob.Interop;

namespace Mesob.Studio;

/// <summary>
/// Everything Studio Control does, independent of the screens. Each method is one action the
/// content creator takes. Sync stick layout (see docs/spec-sync.md):
///
///   MESOB/setup-request.json        shop tablet → studio: the tablet's public keys
///   MESOB/setup-shop-N.json         studio → shop tablet: certificates + sealed code secret
///   MESOB/to-shop-N/...             studio → shop: titles, sealed keys, credit packs, signed catalog
///   MESOB/from-shop-N/log.json      shop → studio: the full signed, chained sales log
/// </summary>
public static class Studio
{
    /// <summary>Data folder: `--data &lt;folder&gt;` on the command line, MESOB_STUDIO_DATA, or the default.</summary>
    public static string DataDir { get; } = ArgData() ?? Environment.GetEnvironmentVariable("MESOB_STUDIO_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mesob", "Studio");

    private static string? ArgData()
    {
        var a = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(a, "--data");
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
    public static string TitlesDir => Path.Combine(DataDir, "titles");
    public static string PosterPath(string titleId) => Path.Combine(DataDir, "posters", titleId + ".img");
    public static StudioDb Db { get; private set; } = null!;
    public static SecureStore Store { get; private set; } = null!;

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm");
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static void Init()
    {
        Directory.CreateDirectory(TitlesDir);
        Store = new SecureStore(Path.Combine(DataDir, "keys"), "Studio");
        Db = new StudioDb(Path.Combine(DataDir, "studio.db"));
    }

    // ---------- Studio identity and capacity ----------

    public static bool HasKey => Store.Load("studio") != null;
    public static string? StudioCert => Db.Get("studio_cert");
    public static bool IsReady => HasKey && StudioCert != null && Db.Long("SELECT COUNT(*) FROM capacity") > 0;
    public static long StudioId => Db.Get("studio_id") is { } s ? long.Parse(s) : 0;
    public static long MaxShops => Db.Get("max_shops") is { } s ? long.Parse(s) : 0;

    private static string Secret() => Convert.ToBase64String(Store.Load("studio") ?? throw new InvalidOperationException("Create the studio key first (Studio setup)."));

    public static string StudioPublicHex()
    {
        var pub = Db.Get("studio_public") ?? throw new InvalidOperationException("No studio key yet.");
        return Convert.ToHexString(Convert.FromBase64String(pub)).ToLowerInvariant();
    }

    /// <summary>Create the studio's signing key. Its public half goes to the vendor for certification.</summary>
    public static void CreateKey()
    {
        if (HasKey) throw new InvalidOperationException("This studio already has a key. Creating a new one would orphan every shop and title.");
        var k = MesobCore.Call("studio.keygen");
        Store.Save("studio", Convert.FromBase64String(k["secret"]!.GetValue<string>()));
        Db.Set("studio_public", k["public"]!.GetValue<string>());
    }

    public static void ImportStudioCert(byte[] cert)
    {
        var d = MesobCore.Call("record.decode", new JsonObject { ["record"] = Convert.ToBase64String(cert) });
        if (d["type"]!.GetValue<string>() != "studio_cert") throw new InvalidOperationException("That file is not a studio certificate.");
        if (d["studio_key"]!.GetValue<string>() != Db.Get("studio_public")) throw new InvalidOperationException("That certificate was made for a different studio key.");
        Db.Set("studio_cert", Convert.ToBase64String(cert));
        Db.Set("studio_id", d["studio_id"]!.ToString());
        Db.Set("max_shops", d["max_shops"]!.ToString());
    }

    public static string ImportCapacity(byte[] block)
    {
        var d = MesobCore.Call("record.decode", new JsonObject { ["record"] = Convert.ToBase64String(block) });
        if (d["type"]!.GetValue<string>() != "capacity") throw new InvalidOperationException("That file is not a capacity block.");
        if (d["studio_id"]!.GetValue<long>() != StudioId) throw new InvalidOperationException("That capacity block is for another studio.");
        var id = d["block_id"]!.GetValue<long>();
        var start = d["serial_start"]!.GetValue<long>();
        var count = d["serial_count"]!.GetValue<long>();
        Db.Exec("INSERT OR IGNORE INTO capacity(block_id,record,serial_start,serial_count,next_serial,added) VALUES($1,$2,$3,$4,$3,$5)",
            id, Convert.ToBase64String(block), start, count, Now());
        return $"Capacity block {id}: {count:N0} licenses.";
    }

    public static long CapacityLeft() => Db.Long("SELECT COALESCE(SUM(serial_start + serial_count - next_serial),0) FROM capacity");

    // ---------- Titles ----------

    public static string AddTitle(string input, string kind, string titleEn, string titleTi, string artist, long price, string? posterPath)
    {
        var mime = kind switch { "audio" => "audio/mp4", "pdf" => "application/pdf", _ => "video/mp4" };
        var tmp = Path.Combine(TitlesDir, $"new-{Guid.NewGuid():N}.msb");
        var req = new JsonObject
        {
            ["studio_secret"] = Secret(),
            ["studio_cert"] = StudioCert ?? throw new InvalidOperationException("Import the studio certificate first."),
            ["kind"] = kind,
            ["mime"] = mime,
            ["title_en"] = titleEn,
            ["title_ti"] = titleTi,
            ["artist"] = artist,
            ["input"] = input,
            ["output"] = tmp,
        };
        if (posterPath != null) req["poster"] = Convert.ToBase64String(File.ReadAllBytes(posterPath));
        var p = MesobCore.Call("studio.package", req);
        var id = p["title_id"]!.GetValue<string>();
        var file = Path.Combine(TitlesDir, id + ".msb");
        File.Move(tmp, file);
        if (posterPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PosterPath(id))!);
            File.Copy(posterPath, PosterPath(id), true);
        }
        var keyLocked = Convert.ToBase64String(Store.Protect(Convert.FromBase64String(p["title_key"]!.GetValue<string>())));
        Db.Exec("INSERT INTO titles(title_id,title_en,title_ti,artist,kind,mime,size,price,file,key_locked,created) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)",
            id, titleEn, titleTi, artist, kind, mime, p["size"]!.GetValue<long>(), price, file, keyLocked, Now());
        return id;
    }

    /// <summary>Open a packaged title for playback in Studio Control (decrypted in memory only).</summary>
    public static DecryptingStream OpenPreview(string titleId)
    {
        var t = Db.Rows("SELECT file, key_locked FROM titles WHERE title_id=$1", titleId).FirstOrDefault() ?? throw new InvalidOperationException("Unknown title.");
        var file = (string)t["file"]!;
        if (!File.Exists(file)) throw new InvalidOperationException($"The encrypted file is missing: {file}");
        var key = Convert.ToBase64String(Store.Unprotect(Convert.FromBase64String((string)t["key_locked"]!)));
        return DecryptingStream.OpenPreview(file, key);
    }

    // ---------- Shops ----------

    /// <summary>Set up a shop from the tablet's setup request. Writes setup-shop-N.json next to the request.</summary>
    public static (long shopId, string setupFile) SetUpShop(string requestPath, string name, string town, string phone)
    {
        var req = JsonNode.Parse(File.ReadAllText(requestPath))!;
        var signPub = req["sign_public"]!.GetValue<string>();
        var kexPub = req["kex_public"]!.GetValue<string>();
        if (Db.Long("SELECT COUNT(*) FROM shops WHERE sign_public=$1", signPub) > 0)
            throw new InvalidOperationException("This tablet is already set up as a shop.");
        var shopId = Db.Long("SELECT COALESCE(MAX(shop_id),0)+1 FROM shops");
        if (shopId > MaxShops) throw new InvalidOperationException($"This studio is licensed for {MaxShops} shops. Ask the vendor for a larger certificate.");
        var cert = MesobCore.Call("studio.shop_cert", new JsonObject
        {
            ["studio_secret"] = Secret(),
            ["studio_cert"] = StudioCert,
            ["shop_id"] = shopId,
            ["sign_public"] = signPub,
            ["kex_public"] = kexPub,
        })["cert"]!.GetValue<string>();
        var secret = MesobCore.Call("studio.secret32")["secret"]!.GetValue<string>();
        var sealedSecret = MesobCore.Call("studio.seal_secret", new JsonObject { ["shop_id"] = shopId, ["kex_public"] = kexPub, ["secret"] = secret })["sealed"]!.GetValue<string>();
        // The request (with the tablet's key attestation chain) is kept for audit.
        Db.Exec("INSERT INTO shops(shop_id,name,town,phone,sign_public,kex_public,cert,secret_locked,setup_request,created) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
            shopId, name, town, phone, signPub, kexPub, cert, Convert.ToBase64String(Store.Protect(Convert.FromBase64String(secret))), req.ToJsonString(), Now());
        var setup = new JsonObject
        {
            ["shop_id"] = shopId,
            ["shop_name"] = name,
            ["studio_cert"] = StudioCert,
            ["shop_cert"] = cert,
            ["sealed_secret"] = sealedSecret,
        };
        var outFile = Path.Combine(Path.GetDirectoryName(requestPath)!, $"setup-shop-{shopId}.json");
        File.WriteAllText(outFile, setup.ToJsonString(Pretty));
        return (shopId, outFile);
    }

    private static string ShopSecret(long shopId)
    {
        var locked = Db.Scalar("SELECT secret_locked FROM shops WHERE shop_id=$1", shopId) as string ?? throw new InvalidOperationException("Unknown shop.");
        return Convert.ToBase64String(Store.Unprotect(Convert.FromBase64String(locked)));
    }

    // ---------- Credits, cash and codes ----------

    /// <summary>Sign a credit pack (locked until its unlock code is given). Serials come from the capacity bought from the vendor.</summary>
    public static long CreatePack(long shopId, long count, long wholesale)
    {
        var block = Db.Rows("SELECT * FROM capacity WHERE serial_start + serial_count - next_serial >= $1 ORDER BY block_id LIMIT 1", count).FirstOrDefault()
            ?? throw new InvalidOperationException($"Not enough capacity left for {count} licenses ({CapacityLeft():N0} left). Buy more from the vendor.");
        var packId = Db.Long("SELECT COALESCE(MAX(pack_id),0)+1 FROM packs");
        var start = Convert.ToInt64(block["next_serial"]);
        var pack = MesobCore.Call("studio.credit_pack", new JsonObject
        {
            ["studio_secret"] = Secret(),
            ["studio_cert"] = StudioCert,
            ["capacity"] = (string)block["record"]!,
            ["pack_id"] = packId,
            ["shop_id"] = shopId,
            ["serial_start"] = start,
            ["serial_count"] = count,
        })["pack"]!.GetValue<string>();
        using var tx = Db.Begin();
        Db.Exec("UPDATE capacity SET next_serial=$1 WHERE block_id=$2", start + count, block["block_id"]);
        Db.Exec("INSERT INTO packs(pack_id,shop_id,block_id,serial_start,serial_count,record,wholesale,created) VALUES($1,$2,$3,$4,$5,$6,$7,$8)",
            packId, shopId, block["block_id"], start, count, pack, wholesale, Now());
        tx.Commit();
        return packId;
    }

    /// <summary>Record the cash and produce the code that unlocks the pack on the shop tablet.</summary>
    public static string Unlock(long packId, long cashReceived, string note)
    {
        var p = Db.Rows("SELECT shop_id FROM packs WHERE pack_id=$1", packId).FirstOrDefault() ?? throw new InvalidOperationException("Unknown pack.");
        var shopId = Convert.ToInt64(p["shop_id"]);
        var code = MesobCore.Call("code.unlock", new JsonObject { ["secret"] = ShopSecret(shopId), ["shop_id"] = shopId, ["pack_id"] = packId })["code"]!.GetValue<string>();
        using var tx = Db.Begin();
        Db.Exec("UPDATE packs SET unlocked=1 WHERE pack_id=$1", packId);
        if (cashReceived > 0) Db.Exec("INSERT INTO payments(shop_id,amount,note,created) VALUES($1,$2,$3,$4)", shopId, cashReceived, note, Now());
        tx.Commit();
        return code;
    }

    /// <summary>Show the unlock code again (for an already unlocked pack), without recording cash.</summary>
    public static string CodeFor(long packId)
    {
        var shopId = Db.Long("SELECT shop_id FROM packs WHERE pack_id=$1", packId);
        return MesobCore.Call("code.unlock", new JsonObject { ["secret"] = ShopSecret(shopId), ["shop_id"] = shopId, ["pack_id"] = packId })["code"]!.GetValue<string>();
    }

    /// <summary>Read a daily report code sent by SMS or phone.</summary>
    public static string EnterReport(long shopId, string code)
    {
        var r = MesobCore.Call("code.read_report", new JsonObject { ["secret"] = ShopSecret(shopId), ["shop_id"] = shopId, ["code"] = code });
        Db.Exec("INSERT OR REPLACE INTO reports(shop_id,day,count,cash,log_tag,received) VALUES($1,$2,$3,$4,$5,$6)",
            shopId, r["day"]!.GetValue<long>(), r["count"]!.GetValue<long>(), r["cash"]!.GetValue<long>(), r["log_tag"]!.GetValue<string>(), Now());
        CheckReports();
        return $"Shop {shopId}, day {r["day"]}: {r["count"]} sales, {r["cash"]!.GetValue<long>():N0} Nakfa.";
    }

    // ---------- Sync sticks ----------

    /// <summary>Write everything shop N needs onto a stick: titles, their keys sealed to that shop, credit packs, signed catalog.</summary>
    public static string WriteSync(string stickRoot, long shopId)
    {
        var shop = Db.Rows("SELECT * FROM shops WHERE shop_id=$1", shopId).Single();
        if (Convert.ToInt64(shop["blocked"]) == 1) throw new InvalidOperationException("This shop is blocked.");
        var dir = Path.Combine(stickRoot, "MESOB", $"to-shop-{shopId}");
        foreach (var sub in new[] { "capacity", "packs", "titles", "keys" }) Directory.CreateDirectory(Path.Combine(dir, sub));
        File.WriteAllBytes(Path.Combine(dir, "studio.cert"), Convert.FromBase64String(StudioCert!));
        File.WriteAllBytes(Path.Combine(dir, "shop.cert"), Convert.FromBase64String((string)shop["cert"]!));
        foreach (var c in Db.Rows("SELECT * FROM capacity WHERE block_id IN (SELECT block_id FROM packs WHERE shop_id=$1)", shopId))
            File.WriteAllBytes(Path.Combine(dir, "capacity", $"{c["block_id"]}.bin"), Convert.FromBase64String((string)c["record"]!));
        foreach (var p in Db.Rows("SELECT * FROM packs WHERE shop_id=$1", shopId))
            File.WriteAllBytes(Path.Combine(dir, "packs", $"{p["pack_id"]}.bin"), Convert.FromBase64String((string)p["record"]!));

        var titles = new JsonArray();
        var copied = 0;
        foreach (var t in Db.Rows("SELECT * FROM titles WHERE active=1 ORDER BY title_en"))
        {
            var id = (string)t["title_id"]!;
            var src = (string)t["file"]!;
            var dst = Path.Combine(dir, "titles", id + ".msb");
            if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length) { File.Copy(src, dst, true); copied++; }
            var key = Convert.ToBase64String(Store.Unprotect(Convert.FromBase64String((string)t["key_locked"]!)));
            var sealedKey = MesobCore.Call("studio.grant", new JsonObject { ["title_id"] = id, ["title_key"] = key, ["kex_public"] = (string)shop["kex_public"]! })["sealed"]!.GetValue<string>();
            File.WriteAllBytes(Path.Combine(dir, "keys", id + ".sealed"), Convert.FromBase64String(sealedKey));
            titles.Add(new JsonObject
            {
                ["title_id"] = id, ["title_en"] = (string?)t["title_en"], ["title_ti"] = (string?)t["title_ti"], ["artist"] = (string?)t["artist"],
                ["kind"] = (string?)t["kind"], ["size"] = Convert.ToInt64(t["size"]), ["price"] = Convert.ToInt64(t["price"]),
            });
        }
        var catalog = Encoding.UTF8.GetBytes(new JsonObject { ["studio_id"] = StudioId, ["shop_id"] = shopId, ["written"] = Now(), ["titles"] = titles }.ToJsonString(Pretty));
        var sig = MesobCore.Call("studio.sign_catalog", new JsonObject { ["studio_secret"] = Secret(), ["data"] = Convert.ToBase64String(catalog) })["sig"]!.GetValue<string>();
        File.WriteAllBytes(Path.Combine(dir, "catalog.json"), catalog);
        File.WriteAllBytes(Path.Combine(dir, "catalog.sig"), Convert.FromBase64String(sig));
        return $"Shop {shopId}: {titles.Count} titles ({copied} new files), {Db.Long("SELECT COUNT(*) FROM packs WHERE shop_id=$1", shopId)} credit packs written.";
    }

    /// <summary>Read every shop's returned sales log from a stick. Each log is checked end to end.</summary>
    public static List<string> ReadReturns(string stickRoot)
    {
        var results = new List<string>();
        var root = Path.Combine(stickRoot, "MESOB");
        if (!Directory.Exists(root)) return ["No MESOB folder on this stick."];
        foreach (var dir in Directory.GetDirectories(root, "from-shop-*"))
        {
            var file = Path.Combine(dir, "log.json");
            if (!File.Exists(file)) continue;
            try { results.Add(ImportLog(JsonNode.Parse(File.ReadAllText(file))!)); }
            catch (Exception ex) { results.Add($"{Path.GetFileName(dir)}: REJECTED, {ex.Message}"); Db.Alert($"Sales log from {Path.GetFileName(dir)} rejected: {ex.Message}"); }
        }
        CheckReports();
        return results.Count > 0 ? results : ["No shop sales logs found on this stick."];
    }

    private static string ImportLog(JsonNode log)
    {
        var shopId = log["shop_id"]!.GetValue<long>();
        var shop = Db.Rows("SELECT * FROM shops WHERE shop_id=$1", shopId).FirstOrDefault() ?? throw new InvalidOperationException($"unknown shop {shopId}");
        var entries = log["entries"]!.AsArray();
        // The shop sends its whole log; check the entire chain from the first entry.
        var v = MesobCore.Call("log.verify", new JsonObject { ["shop_cert"] = (string)shop["cert"]!, ["entries"] = entries.DeepClone(), ["start_seq"] = 0 });
        var known = Convert.ToInt64(shop["log_next_seq"]);
        var added = 0;
        using (var tx = Db.Begin())
        {
            foreach (var d in v["detail"]!.AsArray())
            {
                var seq = d!["seq"]!.GetValue<long>();
                var existing = Db.Scalar("SELECT hash FROM sales WHERE shop_id=$1 AND seq=$2", shopId, seq) as string;
                if (existing != null)
                {
                    if (existing != d["hash"]!.GetValue<string>())
                        throw new InvalidOperationException($"entry {seq} differs from the copy received earlier (log was rewritten)");
                    continue;
                }
                Db.Exec("INSERT INTO sales(shop_id,seq,kind,serial,title_id,device,price,local_time,hash,imported) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
                    shopId, seq, d["kind"]!.GetValue<string>(), d["serial"]!.GetValue<long>(), d["title_id"]!.GetValue<string>(),
                    d["device"]!.GetValue<string>(), d["price"]!.GetValue<long>(), d["local_time"]!.GetValue<long>(), d["hash"]!.GetValue<string>(), Now());
                added++;
            }
            if (v["next_seq"]!.GetValue<long>() < known)
                throw new InvalidOperationException($"log is shorter than before ({v["next_seq"]} entries, studio already had {known})");
            Db.Exec("UPDATE shops SET log_next_seq=$1 WHERE shop_id=$2", v["next_seq"]!.GetValue<long>(), shopId);
            tx.Commit();
        }
        CheckSales(shopId);
        return $"Shop {shopId}: log checked, {added} new entries ({v["sales"]} sales, {v["cash"]!.GetValue<long>():N0} Nakfa in total).";
    }

    /// <summary>Fraud checks after every import: a serial sold twice, or sold outside the shop's packs.</summary>
    private static void CheckSales(long shopId)
    {
        foreach (var d in Db.Rows("SELECT serial, COUNT(*) n, GROUP_CONCAT(DISTINCT shop_id) shops FROM sales WHERE kind='sale' GROUP BY serial HAVING n > 1"))
            Db.Alert($"License serial {d["serial"]} was sold {d["n"]} times (shops {d["shops"]}). A shop key may have been copied.");
        foreach (var d in Db.Rows("SELECT s.serial FROM sales s WHERE s.shop_id=$1 AND NOT EXISTS (SELECT 1 FROM packs p WHERE p.shop_id=s.shop_id AND s.serial >= p.serial_start AND s.serial < p.serial_start + p.serial_count)", shopId))
            Db.Alert($"Shop {shopId} sold serial {d["serial"]}, which is not in any of its credit packs.");
        foreach (var d in Db.Rows("SELECT s.serial FROM sales s JOIN packs p ON p.shop_id=s.shop_id AND s.serial >= p.serial_start AND s.serial < p.serial_start + p.serial_count WHERE s.shop_id=$1 AND p.unlocked=0", shopId))
            Db.Alert($"Shop {shopId} sold serial {d["serial"]} from a pack that has not been paid for and unlocked.");
    }

    /// <summary>Each daily code carries the start of the log's latest fingerprint; the log must contain it.</summary>
    private static void CheckReports()
    {
        foreach (var r in Db.Rows("SELECT * FROM reports"))
        {
            var shopId = Convert.ToInt64(r["shop_id"]);
            var tag = (string)r["log_tag"]!;
            var haveLog = Db.Long("SELECT COUNT(*) FROM sales WHERE shop_id=$1", shopId) > 0;
            if (!haveLog || Convert.ToInt64(r["count"]) == 0) continue;
            var found = Db.Long("SELECT COUNT(*) FROM sales WHERE shop_id=$1 AND substr(hash,1,6)=$2", shopId, tag) > 0;
            var latestSeq = Db.Long("SELECT COALESCE(MAX(seq),-1) FROM sales WHERE shop_id=$1", shopId);
            if (!found && Db.Long("SELECT COUNT(*) FROM sales WHERE shop_id=$1", shopId) >= Convert.ToInt64(r["count"]) && latestSeq >= 0)
                Db.Alert($"Shop {shopId} day {r["day"]}: the report code doesn't match the sales log received. Entries may be missing.");
        }
    }

    // ---------- Reports ----------

    public static string CsvEscape(object? v)
    {
        var s = Convert.ToString(v) ?? "";
        return s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
