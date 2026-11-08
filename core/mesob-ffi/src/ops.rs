//! JSON operations. Request: `{"op": "...", ...fields}`. Response: `{"ok": true, "result": ...}`
//! or `{"ok": false, "error": "..."}`. Binary values are standard base64; ids are hex.
//!
//! Operation names are grouped by who uses them:
//!
//! | prefix      | used by                   |
//! |-------------|---------------------------|
//! | `core.`     | everyone                  |
//! | `device.`, `title.inspect`, `license.verify`, `request.make`, `session.open` | Player |
//! | `shop.`, `log.append`, `request.parse`, `code.read_unlock`, `code.report`     | Shop app |
//! | `studio.`, `log.verify`, `code.unlock`, `code.read_report`, `record.decode`   | Studio Control |
//! | `vendor.`   | Vendor Console            |

use crate::sessions;
use base64::engine::general_purpose::STANDARD as B64;
use base64::Engine;
use mesob_core::bundle::{issue_license, LicenseBundle};
use mesob_core::codes::{self, Report};
use mesob_core::keys::{device_fingerprint, DeviceSecret, KexSecret, PubKey, SignKey};
use mesob_core::log::{self, verify_chain, SaleEntry};
use mesob_core::records::{range_within, Body, CapacityBlock, CreditPack, ShopCert, Signed, StudioCert};
use mesob_core::bundle::license_body;
use mesob_core::seal::{open_at_shop, open_at_shop_shared, seal_to_shop, sealed_ephemeral, shop_ctx};
use mesob_core::title::{package, Kind, TitleFile, TitleHeader, TitleMeta, DEFAULT_CHUNK};
use serde_json::{json, Map, Value};
use std::fs::File;
use std::io::{BufReader, BufWriter};

type Res<T> = Result<T, String>;

#[cfg(not(any(feature = "lab", mesob_vendor)))]
compile_error!("Set MESOB_VENDOR_PUB to the vendor root public key (66 hex characters), or build a development core with `--features lab`.");

const COMPILED_VENDOR: Option<&str> = option_env!("MESOB_VENDOR_PUB");
const CATALOG_DOMAIN: &str = "mesob/catalog/v1";

fn secret_ctx(shop_id: u32) -> Vec<u8> {
    [b"shop-code-secret".as_slice(), &shop_id.to_be_bytes()].concat()
}

/// Handle one JSON request and return the JSON response. Never panics across the boundary.
pub fn dispatch(request: &str) -> String {
    let out = std::panic::catch_unwind(|| run(request)).unwrap_or_else(|_| Err("internal error".into()));
    match out {
        Ok(v) => json!({"ok": true, "result": v}).to_string(),
        Err(e) => json!({"ok": false, "error": e}).to_string(),
    }
}

struct Req(Map<String, Value>);

impl Req {
    fn str(&self, k: &str) -> Res<&str> {
        self.0.get(k).and_then(Value::as_str).ok_or_else(|| format!("missing text field '{k}'"))
    }
    fn opt_str(&self, k: &str) -> Option<&str> {
        self.0.get(k).and_then(Value::as_str)
    }
    fn u64(&self, k: &str) -> Res<u64> {
        self.0.get(k).and_then(Value::as_u64).ok_or_else(|| format!("missing number field '{k}'"))
    }
    fn u32(&self, k: &str) -> Res<u32> {
        u32::try_from(self.u64(k)?).map_err(|_| format!("'{k}' is too large"))
    }
    fn u16(&self, k: &str) -> Res<u16> {
        u16::try_from(self.u64(k)?).map_err(|_| format!("'{k}' is too large"))
    }
    fn bytes(&self, k: &str) -> Res<Vec<u8>> {
        B64.decode(self.str(k)?).map_err(|_| format!("'{k}' is not base64"))
    }
    fn arr32(&self, k: &str) -> Res<[u8; 32]> {
        self.bytes(k)?.try_into().map_err(|_| format!("'{k}' must be 32 bytes"))
    }
    fn hex16(&self, k: &str) -> Res<[u8; 16]> {
        hex::decode(self.str(k)?).ok().and_then(|v| v.try_into().ok()).ok_or_else(|| format!("'{k}' must be 32 hex characters"))
    }
    fn hex32(&self, k: &str) -> Res<[u8; 32]> {
        hex::decode(self.str(k)?).ok().and_then(|v| v.try_into().ok()).ok_or_else(|| format!("'{k}' must be 64 hex characters"))
    }
    fn record<T: Body>(&self, k: &str) -> Res<Signed<T>> {
        Signed::decode(&self.bytes(k)?).map_err(|e| format!("'{k}': {e}"))
    }
    fn sign_key(&self, k: &str) -> Res<SignKey> {
        SignKey::from_bytes(&self.arr32(k)?).map_err(e)
    }
    fn pub_key(&self, k: &str) -> Res<PubKey> {
        PubKey::from_slice(&self.bytes(k)?).map_err(e)
    }
    fn vendor(&self) -> Res<PubKey> {
        if let Some(h) = COMPILED_VENDOR {
            return PubKey::from_slice(&hex::decode(h).map_err(|_| "bad compiled vendor key")?).map_err(e);
        }
        #[cfg(feature = "lab")]
        {
            PubKey::from_slice(&hex::decode(self.str("vendor_pub")?).map_err(|_| "vendor_pub must be hex")?).map_err(e)
        }
        #[cfg(not(feature = "lab"))]
        {
            Err("no vendor key in this build".into())
        }
    }
    /// Open a title file from a path, or (Android) from a file descriptor the caller hands over.
    fn file(&self) -> Res<File> {
        if let Some(p) = self.opt_str("path") {
            return File::open(p).map_err(|err| format!("{p}: {err}"));
        }
        #[cfg(unix)]
        if let Some(fd) = self.0.get("fd").and_then(Value::as_i64) {
            use std::os::fd::FromRawFd;
            // SAFETY: the caller transfers ownership of this descriptor (ParcelFileDescriptor.detachFd).
            return Ok(unsafe { File::from_raw_fd(fd as i32) });
        }
        Err("missing 'path' (or 'fd' on Android)".into())
    }
}

/// Open a key sealed to this shop: with a software key-exchange key (`kex_secret`), or with the
/// ECDH shared secret the tablet's Keystore computed (`kex_shared`) for the shop key `kex_pub`.
fn open_shop_sealed(r: &Req, sealed: &[u8], ctx: &[u8], kex_pub: PubKey) -> Res<[u8; 32]> {
    if r.opt_str("kex_secret").is_some() {
        let kex = KexSecret::from_bytes(&r.arr32("kex_secret")?).map_err(e)?;
        return open_at_shop(&kex, sealed, ctx).map_err(e);
    }
    open_at_shop_shared(&r.bytes("kex_shared")?, sealed, &kex_pub, ctx).map_err(e)
}

fn e(err: impl std::fmt::Display) -> String {
    err.to_string()
}
fn b64(b: &[u8]) -> String {
    B64.encode(b)
}

fn header_json(h: &TitleHeader) -> Value {
    json!({
        "title_id": hex::encode(h.title_id),
        "studio_id": h.studio_id,
        "kind": match h.kind { Kind::Video => "video", Kind::Audio => "audio", Kind::Pdf => "pdf" },
        "mime": h.mime,
        "title_en": h.title_en,
        "title_ti": h.title_ti,
        "artist": h.artist,
        "size": h.plain_size,
        "poster": b64(&h.poster),
    })
}

fn bundle_json(b: &LicenseBundle) -> Value {
    let l = &b.license.body;
    json!({
        "serial": l.serial,
        "title_id": hex::encode(l.title_id),
        "device": hex::encode(l.device_fp),
        "shop_id": l.shop_id,
        "pack_id": l.pack_id,
        "studio_id": b.studio.body.studio_id,
        "reissue": l.flags & mesob_core::records::LICENSE_FLAG_REISSUE != 0,
    })
}

/// Device request (for the "Buy at a shop" QR): "MSR1" | device public key | n | title ids.
fn make_request(device_pub: &[u8; 32], titles: &[[u8; 16]]) -> Vec<u8> {
    let mut v = b"MSR1".to_vec();
    v.extend_from_slice(device_pub);
    v.push(titles.len() as u8);
    for t in titles {
        v.extend_from_slice(t);
    }
    v
}

fn parse_request(b: &[u8]) -> Res<([u8; 32], Vec<[u8; 16]>)> {
    if b.len() < 37 || &b[..4] != b"MSR1" {
        return Err("not a Mesob device request".into());
    }
    let n = b[36] as usize;
    if b.len() != 37 + 16 * n || n == 0 {
        return Err("malformed device request".into());
    }
    let device: [u8; 32] = b[4..36].try_into().expect("32 bytes");
    let titles = b[37..].chunks(16).map(|c| c.try_into().expect("16 bytes")).collect();
    Ok((device, titles))
}

fn run(request: &str) -> Res<Value> {
    let v: Value = serde_json::from_str(request).map_err(|_| "request is not JSON")?;
    let Value::Object(map) = v else { return Err("request must be a JSON object".into()) };
    let r = Req(map);
    match r.str("op")? {
        // ---------- everyone ----------
        "core.info" => Ok(json!({
            "version": env!("CARGO_PKG_VERSION"),
            "lab": COMPILED_VENDOR.is_none(),
            "vendor_key_id": COMPILED_VENDOR.and_then(|h| hex::decode(h).ok()).and_then(|b| PubKey::from_slice(&b).ok()).map(|k| hex::encode(k.key_id())),
        })),

        // ---------- Player ----------
        "device.create" => {
            let d = DeviceSecret::generate();
            Ok(json!({"secret": b64(&d.to_bytes()), "public": b64(&d.public()), "fingerprint": hex::encode(d.fingerprint())}))
        }
        "device.info" => {
            let d = DeviceSecret::from_bytes(r.arr32("secret")?);
            Ok(json!({"public": b64(&d.public()), "fingerprint": hex::encode(d.fingerprint())}))
        }
        "title.inspect" => {
            let mut t = TitleFile::open(BufReader::new(r.file()?), &r.vendor()?).map_err(e)?;
            if r.0.get("check").and_then(Value::as_bool).unwrap_or(false) {
                t.check_integrity().map_err(e)?;
            }
            Ok(header_json(&t.header.body))
        }
        "license.verify" => {
            let b = LicenseBundle::decode(&r.bytes("bundle")?).map_err(e)?;
            let d = DeviceSecret::from_bytes(r.arr32("device_secret")?);
            b.verify(&r.vendor()?, &d.fingerprint()).map_err(e)?;
            Ok(bundle_json(&b))
        }
        "request.make" => {
            let d = DeviceSecret::from_bytes(r.arr32("device_secret")?);
            let ids = r.0.get("title_ids").and_then(Value::as_array).ok_or("missing 'title_ids'")?;
            let mut titles = vec![];
            for id in ids {
                let h = id.as_str().ok_or("title ids must be hex text")?;
                titles.push(hex::decode(h).ok().and_then(|v| v.try_into().ok()).ok_or("bad title id")?);
            }
            if titles.is_empty() || titles.len() > 20 {
                return Err("a request holds 1 to 20 titles".into());
            }
            Ok(json!({"request": b64(&make_request(&d.public(), &titles))}))
        }
        "session.open" => {
            let vendor = r.vendor()?;
            let b = LicenseBundle::decode(&r.bytes("bundle")?).map_err(e)?;
            let d = DeviceSecret::from_bytes(r.arr32("device_secret")?);
            let t = TitleFile::open(BufReader::new(r.file()?), &vendor).map_err(e)?;
            let header = header_json(&t.header.body);
            let reader = b.open_title(t, &vendor, &d).map_err(e)?;
            let size = reader.len();
            let handle = sessions::insert(reader);
            Ok(json!({"handle": handle, "size": size, "title": header, "license": bundle_json(&b)}))
        }

        // ---------- Shop app ----------
        "request.parse" => {
            let (device, titles) = parse_request(&r.bytes("request")?)?;
            Ok(json!({
                "device_public": b64(&device),
                "device": hex::encode(device_fingerprint(&device)),
                "title_ids": titles.iter().map(hex::encode).collect::<Vec<_>>(),
            }))
        }
        // Shops with a hardware signing key: prepare → sign in Keystore → finish.
        "shop.issue_prepare" => {
            let shop: Signed<ShopCert> = r.record("shop_cert")?;
            let pack: Signed<CreditPack> = r.record("pack")?;
            let title_id = r.hex16("title_id")?;
            let title_key = open_shop_sealed(&r, &r.bytes("sealed_title_key")?, &shop_ctx(&title_id), shop.body.kex_key)?;
            let flags = r.0.get("reissue").and_then(Value::as_bool).unwrap_or(false) as u8;
            let body = license_body(&shop, &pack, r.u64("serial")?, title_id, &title_key, &r.arr32("device_public")?, flags).map_err(e)?;
            let (unsigned, msg) = Signed::prepare(&body);
            Ok(json!({"body": b64(&unsigned), "to_sign": b64(&msg)}))
        }
        "shop.issue_finish" => {
            let shop: Signed<ShopCert> = r.record("shop_cert")?;
            let license = Signed::finish(&r.bytes("body")?, &r.bytes("sig")?, &shop.body.sign_key).map_err(e)?;
            let device: [u8; 32] = r.arr32("device_public")?;
            let bundle = LicenseBundle { studio: r.record("studio_cert")?, capacity: r.record("capacity")?, shop, pack: r.record("pack")?, license };
            bundle.verify(&r.vendor()?, &device_fingerprint(&device)).map_err(e)?;
            Ok(json!({"bundle": b64(&bundle.encode()), "license": bundle_json(&bundle)}))
        }
        "log.prepare" => {
            let prev: Option<Signed<SaleEntry>> = match r.opt_str("prev") {
                Some(_) => Some(r.record("prev")?),
                None => None,
            };
            let kind = match r.str("kind")? {
                "sale" => log::KIND_SALE,
                "reissue" => log::KIND_REISSUE,
                _ => return Err("kind must be sale or reissue".into()),
            };
            let body = log::next_entry(prev.as_ref(), kind, r.u64("serial")?, r.hex16("title_id")?, r.hex16("device")?, r.u32("price")?, r.u64("local_time").unwrap_or(0));
            let seq = body.seq;
            let (unsigned, msg) = Signed::prepare(&body);
            Ok(json!({"body": b64(&unsigned), "to_sign": b64(&msg), "seq": seq}))
        }
        "log.finish" => {
            let shop: Signed<ShopCert> = r.record("shop_cert")?;
            let entry: Signed<SaleEntry> = Signed::finish(&r.bytes("body")?, &r.bytes("sig")?, &shop.body.sign_key).map_err(e)?;
            Ok(json!({"entry": b64(&entry.encode()), "seq": entry.body.seq, "hash": hex::encode(log::entry_hash(&entry))}))
        }
        "shop.sealed_eph" => {
            let k = sealed_ephemeral(&r.bytes("sealed")?).map_err(e)?;
            Ok(json!({"eph_spki": b64(&k.to_spki().map_err(e)?)}))
        }
        "key.spki_to_sec1" => Ok(json!({"public": b64(&PubKey::from_spki(&r.bytes("spki")?).map_err(e)?.0)})),
        "key.sec1_to_spki" => Ok(json!({"spki": b64(&r.pub_key("public")?.to_spki().map_err(e)?)})),

        "shop.issue" => {
            let sign = r.sign_key("sign_secret")?;
            let shop: Signed<ShopCert> = r.record("shop_cert")?;
            let pack: Signed<CreditPack> = r.record("pack")?;
            let title_id = r.hex16("title_id")?;
            let title_key = open_shop_sealed(&r, &r.bytes("sealed_title_key")?, &shop_ctx(&title_id), shop.body.kex_key)?;
            let device: [u8; 32] = r.arr32("device_public")?;
            let flags = r.0.get("reissue").and_then(Value::as_bool).unwrap_or(false) as u8;
            let license = issue_license(&sign, &shop, &pack, r.u64("serial")?, title_id, &title_key, &device, flags).map_err(e)?;
            let bundle = LicenseBundle { studio: r.record("studio_cert")?, capacity: r.record("capacity")?, shop, pack, license };
            bundle.verify(&r.vendor()?, &device_fingerprint(&device)).map_err(e)?;
            Ok(json!({"bundle": b64(&bundle.encode()), "license": bundle_json(&bundle)}))
        }
        "log.append" => {
            let sign = r.sign_key("sign_secret")?;
            let prev: Option<Signed<SaleEntry>> = match r.opt_str("prev") {
                Some(_) => Some(r.record("prev")?),
                None => None,
            };
            let kind = match r.str("kind")? {
                "sale" => log::KIND_SALE,
                "reissue" => log::KIND_REISSUE,
                _ => return Err("kind must be sale or reissue".into()),
            };
            let entry = log::append(
                prev.as_ref(),
                &sign,
                kind,
                r.u64("serial")?,
                r.hex16("title_id")?,
                r.hex16("device")?,
                r.u32("price")?,
                r.u64("local_time").unwrap_or(0),
            );
            Ok(json!({"entry": b64(&entry.encode()), "seq": entry.body.seq, "hash": hex::encode(log::entry_hash(&entry))}))
        }
        "shop.keygen" => {
            let s = SignKey::generate();
            let k = KexSecret::generate();
            Ok(json!({"sign_secret": b64(&s.to_bytes()), "sign_public": b64(&s.public().0), "kex_secret": b64(&k.to_bytes()), "kex_public": b64(&k.public().0)}))
        }
        "shop.open_secret" => {
            let kex_pub = match r.opt_str("kex_public") {
                Some(_) => r.pub_key("kex_public")?,
                None => KexSecret::from_bytes(&r.arr32("kex_secret")?).map_err(e)?.public(),
            };
            let s = open_shop_sealed(&r, &r.bytes("sealed")?, &secret_ctx(r.u32("shop_id")?), kex_pub)?;
            Ok(json!({"secret": b64(&s)}))
        }
        // Check a setup or sync stick's studio material against the vendor root.
        "shop.check_setup" => {
            let vendor = r.vendor()?;
            let sc: Signed<StudioCert> = r.record("studio_cert")?;
            if sc.body.vendor_key_id != vendor.key_id() {
                return Err("studio certificate is from another vendor".into());
            }
            sc.verify(&vendor).map_err(e)?;
            let shop: Signed<ShopCert> = r.record("shop_cert")?;
            shop.verify(&sc.body.studio_key).map_err(e)?;
            if shop.body.sign_key != r.pub_key("sign_public")? || shop.body.kex_key != r.pub_key("kex_public")? {
                return Err("this setup was made for another shop tablet".into());
            }
            Ok(json!({"shop_id": shop.body.shop_id, "studio_id": sc.body.studio_id}))
        }
        "shop.check_pack" => {
            let vendor = r.vendor()?;
            let sc: Signed<StudioCert> = r.record("studio_cert")?;
            sc.verify(&vendor).map_err(e)?;
            let cap: Signed<CapacityBlock> = r.record("capacity")?;
            cap.verify(&vendor).map_err(e)?;
            let pack: Signed<CreditPack> = r.record("pack")?;
            pack.verify(&sc.body.studio_key).map_err(e)?;
            if pack.body.block_id != cap.body.block_id || !range_within(cap.body.serial_start, cap.body.serial_count, pack.body.serial_start, pack.body.serial_count) {
                return Err("credit pack does not fit its capacity block".into());
            }
            Ok(json!({"pack_id": pack.body.pack_id, "shop_id": pack.body.shop_id, "block_id": pack.body.block_id, "serial_start": pack.body.serial_start, "serial_count": pack.body.serial_count}))
        }
        "catalog.verify" => {
            let vendor = r.vendor()?;
            let sc: Signed<StudioCert> = r.record("studio_cert")?;
            sc.verify(&vendor).map_err(e)?;
            let sig: [u8; 64] = r.bytes("sig")?.try_into().map_err(|_| "sig must be 64 bytes")?;
            sc.body.studio_key.verify(CATALOG_DOMAIN, &r.bytes("data")?, &sig).map_err(e)?;
            Ok(json!({"studio_id": sc.body.studio_id}))
        }
        "code.read_unlock" => {
            let pack = codes::read_unlock(&r.arr32("secret")?, r.u32("shop_id")?, r.str("code")?).map_err(e)?;
            Ok(json!({"pack_id": pack}))
        }
        "code.report" => {
            let tag = hex::decode(r.str("log_tag")?).ok().and_then(|v| v.try_into().ok()).ok_or("log_tag must be 6 hex characters")?;
            let rep = Report { day: r.u16("day")?, count: r.u16("count")?, cash: r.u32("cash")?, log_tag: tag };
            Ok(json!({"code": codes::report_code(&r.arr32("secret")?, r.u32("shop_id")?, &rep)}))
        }

        // ---------- Studio Control ----------
        "studio.keygen" => {
            let k = SignKey::generate();
            Ok(json!({"secret": b64(&k.to_bytes()), "public": b64(&k.public().0)}))
        }
        "studio.kexgen" => {
            let k = KexSecret::generate();
            Ok(json!({"secret": b64(&k.to_bytes()), "public": b64(&k.public().0)}))
        }
        "studio.secret32" => Ok(json!({"secret": b64(&mesob_core::keys::random::<32>())})),
        // Seal a 32-byte secret (the shop's code secret) to the shop's key, so it can travel on a
        // setup stick without being readable by anyone else.
        "studio.seal_secret" => {
            let shop_id = r.u32("shop_id")?;
            let sealed = seal_to_shop(&r.pub_key("kex_public")?, &r.arr32("secret")?, &secret_ctx(shop_id)).map_err(e)?;
            Ok(json!({"sealed": b64(&sealed)}))
        }
        "studio.sign_catalog" => {
            let studio = r.sign_key("studio_secret")?;
            Ok(json!({"sig": b64(&studio.sign(CATALOG_DOMAIN, &r.bytes("data")?))}))
        }
        "studio.shop_cert" => {
            let studio = r.sign_key("studio_secret")?;
            let sc: Signed<StudioCert> = r.record("studio_cert")?;
            let shop_id = r.u32("shop_id")?;
            if shop_id == 0 || shop_id > sc.body.max_shops as u32 {
                return Err(format!("this studio is licensed for shops 1 to {}", sc.body.max_shops));
            }
            let cert = Signed::sign(
                ShopCert { shop_id, studio_id: sc.body.studio_id, sign_key: r.pub_key("sign_public")?, kex_key: r.pub_key("kex_public")? },
                &studio,
            );
            Ok(json!({"cert": b64(&cert.encode())}))
        }
        "studio.credit_pack" => {
            let studio = r.sign_key("studio_secret")?;
            let sc: Signed<StudioCert> = r.record("studio_cert")?;
            let cap: Signed<CapacityBlock> = r.record("capacity")?;
            cap.verify(&r.vendor()?).map_err(e)?;
            let (start, count) = (r.u64("serial_start")?, r.u32("serial_count")?);
            if !range_within(cap.body.serial_start, cap.body.serial_count, start, count) {
                return Err("credit pack must sit inside the capacity block".into());
            }
            let pack = Signed::sign(
                CreditPack { pack_id: r.u32("pack_id")?, shop_id: r.u32("shop_id")?, studio_id: sc.body.studio_id, block_id: cap.body.block_id, serial_start: start, serial_count: count },
                &studio,
            );
            Ok(json!({"pack": b64(&pack.encode())}))
        }
        "studio.package" => {
            let studio = r.sign_key("studio_secret")?;
            let sc: Signed<StudioCert> = r.record("studio_cert")?;
            let kind = match r.str("kind")? {
                "video" => Kind::Video,
                "audio" => Kind::Audio,
                "pdf" => Kind::Pdf,
                _ => return Err("kind must be video, audio or pdf".into()),
            };
            let meta = TitleMeta {
                kind,
                mime: r.str("mime")?.to_string(),
                title_en: r.str("title_en")?.to_string(),
                title_ti: r.opt_str("title_ti").unwrap_or("").to_string(),
                artist: r.opt_str("artist").unwrap_or("").to_string(),
                poster: match r.opt_str("poster") {
                    Some(_) => r.bytes("poster")?,
                    None => vec![],
                },
            };
            let input = r.str("input")?;
            let output = r.str("output")?;
            let mut i = BufReader::new(File::open(input).map_err(|err| format!("{input}: {err}"))?);
            let mut o = BufWriter::new(File::create(output).map_err(|err| format!("{output}: {err}"))?);
            let p = package(&mut i, &mut o, &sc, &studio, meta, DEFAULT_CHUNK).map_err(e)?;
            Ok(json!({"title_id": hex::encode(p.title_id), "title_key": b64(&p.title_key), "size": p.plain_size}))
        }
        // Play a title the studio packaged, using the studio's own copy of its key, so staff can
        // check what is inside. The file's signature is checked as on a Player.
        "studio.preview" => {
            let vendor = r.vendor()?;
            let t = TitleFile::open(BufReader::new(r.file()?), &vendor).map_err(e)?;
            let header = header_json(&t.header.body);
            let reader = t.decryptor(r.arr32("title_key")?);
            let size = reader.len();
            Ok(json!({"handle": sessions::insert(reader), "size": size, "title": header}))
        }
        "studio.grant" => {
            let id = r.hex16("title_id")?;
            let sealed = seal_to_shop(&r.pub_key("kex_public")?, &r.arr32("title_key")?, &shop_ctx(&id)).map_err(e)?;
            Ok(json!({"sealed": b64(&sealed)}))
        }
        "code.unlock" => Ok(json!({"code": codes::unlock_code(&r.arr32("secret")?, r.u32("shop_id")?, r.u32("pack_id")?)})),
        "code.read_report" => {
            let rep = codes::read_report(&r.arr32("secret")?, r.u32("shop_id")?, r.str("code")?).map_err(e)?;
            Ok(json!({"day": rep.day, "count": rep.count, "cash": rep.cash, "log_tag": hex::encode(rep.log_tag)}))
        }
        "log.verify" => {
            let shop: Signed<ShopCert> = r.record("shop_cert")?;
            let list = r.0.get("entries").and_then(Value::as_array).ok_or("missing 'entries'")?;
            let mut entries = Vec::with_capacity(list.len());
            for item in list {
                let raw = B64.decode(item.as_str().ok_or("entries must be base64 text")?).map_err(|_| "entry is not base64")?;
                entries.push(Signed::<SaleEntry>::decode(&raw).map_err(e)?);
            }
            let start_prev = match r.opt_str("start_prev") {
                Some(_) => r.hex32("start_prev")?,
                None => [0u8; 32],
            };
            let s = verify_chain(&entries, &shop.body.sign_key, r.u64("start_seq").unwrap_or(0), start_prev).map_err(e)?;
            let detail: Vec<Value> = entries
                .iter()
                .map(|x| json!({"seq": x.body.seq, "kind": if x.body.kind == log::KIND_SALE {"sale"} else {"reissue"}, "serial": x.body.serial, "title_id": hex::encode(x.body.title_id), "device": hex::encode(x.body.device_fp), "price": x.body.price, "local_time": x.body.local_time, "hash": hex::encode(log::entry_hash(x))}))
                .collect();
            Ok(json!({"entries": s.entries, "sales": s.sales, "reissues": s.reissues, "cash": s.cash, "next_seq": s.next_seq, "last_hash": hex::encode(s.last_hash), "detail": detail}))
        }
        "record.decode" => {
            let raw = r.bytes("record")?;
            match raw.first() {
                Some(0x01) => {
                    let c = Signed::<StudioCert>::decode(&raw).map_err(e)?;
                    Ok(json!({"type": "studio_cert", "studio_id": c.body.studio_id, "max_shops": c.body.max_shops, "studio_key": b64(&c.body.studio_key.0)}))
                }
                Some(0x02) => {
                    let c = Signed::<CapacityBlock>::decode(&raw).map_err(e)?;
                    Ok(json!({"type": "capacity", "block_id": c.body.block_id, "studio_id": c.body.studio_id, "serial_start": c.body.serial_start, "serial_count": c.body.serial_count}))
                }
                Some(0x03) => {
                    let c = Signed::<ShopCert>::decode(&raw).map_err(e)?;
                    Ok(json!({"type": "shop_cert", "shop_id": c.body.shop_id, "studio_id": c.body.studio_id, "sign_key": b64(&c.body.sign_key.0), "kex_key": b64(&c.body.kex_key.0)}))
                }
                Some(0x04) => {
                    let c = Signed::<CreditPack>::decode(&raw).map_err(e)?;
                    Ok(json!({"type": "credit_pack", "pack_id": c.body.pack_id, "shop_id": c.body.shop_id, "block_id": c.body.block_id, "serial_start": c.body.serial_start, "serial_count": c.body.serial_count}))
                }
                _ => Err("unknown record".into()),
            }
        }

        // ---------- Vendor Console ----------
        "vendor.studio_cert" => {
            let vendor = r.sign_key("vendor_secret")?;
            let cert = Signed::sign(
                StudioCert { studio_id: r.u32("studio_id")?, studio_key: r.pub_key("studio_public")?, max_shops: r.u16("max_shops")?, features: r.u32("features").unwrap_or(0), vendor_key_id: vendor.public().key_id() },
                &vendor,
            );
            Ok(json!({"cert": b64(&cert.encode())}))
        }
        "vendor.capacity" => {
            let vendor = r.sign_key("vendor_secret")?;
            let block = Signed::sign(
                CapacityBlock { block_id: r.u32("block_id")?, studio_id: r.u32("studio_id")?, serial_start: r.u64("serial_start")?, serial_count: r.u32("serial_count")?, vendor_key_id: vendor.public().key_id() },
                &vendor,
            );
            Ok(json!({"capacity": b64(&block.encode())}))
        }

        other => Err(format!("unknown operation '{other}'")),
    }
}
