//! `mesob-lab`: development toolkit. Plays every role in the chain from the command line so the apps
//! can be built and tested before the real Studio Control and Shop app exist.
//!
//! Keys are written as plain hex files. That is fine for a lab, never for production: production
//! keys live in a hardware security key (vendor), TPM (studio) or Android Keystore (shop).

use clap::{Parser, Subcommand};
use mesob_core::bundle::{issue_license, LicenseBundle};
use mesob_core::codes::{self, Report};
use mesob_core::keys::{device_fingerprint, DeviceSecret, KexSecret, PubKey, SignKey};
use mesob_core::log::{self, verify_chain};
use mesob_core::records::{CapacityBlock, CreditPack, ShopCert, Signed, StudioCert};
use mesob_core::seal::{open_at_shop, seal_to_shop, shop_ctx};
use mesob_core::title::{package, Kind, TitleFile, TitleMeta, DEFAULT_CHUNK};
use std::fs::{self, File};
use std::io::{BufReader, BufWriter, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

type Res<T> = Result<T, Box<dyn std::error::Error>>;

#[derive(Parser)]
#[command(name = "mesob-lab", version, about = "Mesob development toolkit (lab keys only)")]
struct Cli {
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Subcommand)]
enum Cmd {
    /// Create the vendor root key (vendor.key, vendor.pub)
    VendorInit {
        #[arg(long)]
        dir: PathBuf,
    },
    /// Vendor: create a studio key and certificate
    StudioInit {
        #[arg(long)]
        vendor: PathBuf,
        #[arg(long)]
        dir: PathBuf,
        #[arg(long)]
        studio_id: u32,
        #[arg(long, default_value_t = 20)]
        max_shops: u16,
    },
    /// Vendor Console: certify a studio key sent by Studio Control (studio-request.txt)
    VendorCertify {
        #[arg(long)]
        vendor: PathBuf,
        /// The studio's request file (its public key in hex)
        #[arg(long)]
        request: PathBuf,
        #[arg(long)]
        studio_id: u32,
        #[arg(long, default_value_t = 20)]
        max_shops: u16,
        #[arg(long)]
        out: PathBuf,
    },
    /// Vendor Console: sign a capacity block for a studio
    VendorCapacity {
        #[arg(long)]
        vendor: PathBuf,
        #[arg(long)]
        studio_id: u32,
        #[arg(long)]
        block_id: u32,
        #[arg(long)]
        start: u64,
        #[arg(long)]
        count: u32,
        #[arg(long)]
        out: PathBuf,
    },
    /// Vendor: sign a capacity block (a serial range the studio has paid for)
    Capacity {
        #[arg(long)]
        vendor: PathBuf,
        #[arg(long)]
        studio: PathBuf,
        #[arg(long)]
        block_id: u32,
        #[arg(long)]
        start: u64,
        #[arg(long)]
        count: u32,
    },
    /// Studio: set up a shop (keys, certificate, shared code secret)
    ShopInit {
        #[arg(long)]
        studio: PathBuf,
        #[arg(long)]
        dir: PathBuf,
        #[arg(long)]
        shop_id: u32,
    },
    /// Studio: sign a credit pack for a shop out of a capacity block
    Credit {
        #[arg(long)]
        studio: PathBuf,
        #[arg(long)]
        shop: PathBuf,
        #[arg(long)]
        block: PathBuf,
        #[arg(long)]
        pack_id: u32,
        #[arg(long)]
        start: u64,
        #[arg(long)]
        count: u32,
    },
    /// Studio: encrypt a media file into a .msb title
    Package {
        #[arg(long)]
        studio: PathBuf,
        #[arg(long)]
        input: PathBuf,
        #[arg(long)]
        out: PathBuf,
        /// video | audio | pdf
        #[arg(long)]
        kind: String,
        #[arg(long)]
        title_en: String,
        #[arg(long, default_value = "")]
        title_ti: String,
        #[arg(long, default_value = "")]
        artist: String,
        #[arg(long)]
        poster: Option<PathBuf>,
    },
    /// Studio: give a shop the key for a title (sealed to the shop's key)
    Grant {
        #[arg(long)]
        studio: PathBuf,
        #[arg(long)]
        shop: PathBuf,
        #[arg(long)]
        title: PathBuf,
    },
    /// Customer device: create a device key
    DeviceInit {
        #[arg(long)]
        dir: PathBuf,
    },
    /// Shop: sell a title to a device (writes a .msl license bundle and logs the sale)
    Sell {
        #[arg(long)]
        studio: PathBuf,
        #[arg(long)]
        shop: PathBuf,
        #[arg(long)]
        block: PathBuf,
        #[arg(long)]
        pack: PathBuf,
        #[arg(long)]
        title: PathBuf,
        #[arg(long)]
        device_pub: PathBuf,
        #[arg(long)]
        serial: u64,
        #[arg(long, default_value_t = 0)]
        price: u32,
        #[arg(long)]
        out: PathBuf,
    },
    /// Show and check a .msb title or .msl license
    Inspect {
        #[arg(long)]
        vendor: PathBuf,
        file: PathBuf,
    },
    /// Device: check a license and decrypt the title to a file (lab testing only)
    Unlock {
        #[arg(long)]
        vendor: PathBuf,
        #[arg(long)]
        device: PathBuf,
        #[arg(long)]
        license: PathBuf,
        #[arg(long)]
        title: PathBuf,
        #[arg(long)]
        out: PathBuf,
    },
    /// Short codes
    #[command(subcommand)]
    Code(CodeCmd),
    /// Studio: check a shop's sales log and print totals
    VerifyLog {
        #[arg(long)]
        shop: PathBuf,
    },
}

#[derive(Subcommand)]
enum CodeCmd {
    /// Studio: make a credit unlock code
    Unlock {
        #[arg(long)]
        shop: PathBuf,
        #[arg(long)]
        pack_id: u32,
    },
    /// Shop: read a credit unlock code
    ReadUnlock {
        #[arg(long)]
        shop: PathBuf,
        code: String,
    },
    /// Shop: make today's report code from its sales log
    Report {
        #[arg(long)]
        shop: PathBuf,
        #[arg(long)]
        day: u16,
    },
    /// Studio: read a report code
    ReadReport {
        #[arg(long)]
        shop: PathBuf,
        code: String,
    },
}

fn write_hex(p: &Path, b: &[u8]) -> Res<()> {
    fs::write(p, hex::encode(b))?;
    Ok(())
}
fn read_hex(p: &Path) -> Res<Vec<u8>> {
    Ok(hex::decode(fs::read_to_string(p)?.trim()).map_err(|e| format!("{}: {e}", p.display()))?)
}
fn read_key32(p: &Path) -> Res<[u8; 32]> {
    Ok(read_hex(p)?.try_into().map_err(|_| format!("{}: expected 32 bytes", p.display()))?)
}
fn sign_key(p: &Path) -> Res<SignKey> {
    Ok(SignKey::from_bytes(&read_key32(p)?)?)
}
fn pub_key(p: &Path) -> Res<PubKey> {
    Ok(PubKey::from_slice(&read_hex(p)?)?)
}
fn record<T: mesob_core::records::Body>(p: &Path) -> Res<Signed<T>> {
    Ok(Signed::decode(&fs::read(p)?)?)
}
fn open_title(p: &Path, vendor: &PubKey) -> Res<TitleFile<BufReader<File>>> {
    Ok(TitleFile::open(BufReader::new(File::open(p)?), vendor)?)
}
fn vendor_of(studio_dir: &Path) -> Res<PubKey> {
    pub_key(&studio_dir.join("vendor.pub"))
}

fn load_log(shop: &Path) -> Res<Vec<Signed<log::SaleEntry>>> {
    let p = shop.join("sales.log");
    if !p.exists() {
        return Ok(vec![]);
    }
    let data = fs::read(p)?;
    let mut out = vec![];
    let mut i = 0;
    while i < data.len() {
        let n = u16::from_be_bytes([data[i], data[i + 1]]) as usize;
        out.push(Signed::decode(&data[i + 2..i + 2 + n])?);
        i += 2 + n;
    }
    Ok(out)
}

fn save_log(shop: &Path, entries: &[Signed<log::SaleEntry>]) -> Res<()> {
    let mut data = vec![];
    for e in entries {
        let b = e.encode();
        data.extend_from_slice(&(b.len() as u16).to_be_bytes());
        data.extend_from_slice(&b);
    }
    fs::write(shop.join("sales.log"), data)?;
    Ok(())
}

fn run(cli: Cli) -> Res<()> {
    match cli.cmd {
        Cmd::VendorInit { dir } => {
            fs::create_dir_all(&dir)?;
            let k = SignKey::generate();
            write_hex(&dir.join("vendor.key"), &k.to_bytes())?;
            write_hex(&dir.join("vendor.pub"), &k.public().0)?;
            println!("vendor key id {}", hex::encode(k.public().key_id()));
        }
        Cmd::StudioInit { vendor, dir, studio_id, max_shops } => {
            fs::create_dir_all(dir.join("keys"))?;
            let vk = sign_key(&vendor.join("vendor.key"))?;
            let sk = SignKey::generate();
            let cert = Signed::sign(
                StudioCert { studio_id, studio_key: sk.public(), max_shops, features: 0, vendor_key_id: vk.public().key_id() },
                &vk,
            );
            write_hex(&dir.join("studio.key"), &sk.to_bytes())?;
            fs::write(dir.join("studio.cert"), cert.encode())?;
            write_hex(&dir.join("vendor.pub"), &vk.public().0)?;
            println!("studio {studio_id} certified for up to {max_shops} shops");
        }
        Cmd::VendorCertify { vendor, request, studio_id, max_shops, out } => {
            let vk = sign_key(&vendor.join("vendor.key"))?;
            let studio_key = pub_key(&request)?;
            let cert = Signed::sign(
                StudioCert { studio_id, studio_key, max_shops, features: 0, vendor_key_id: vk.public().key_id() },
                &vk,
            );
            fs::write(&out, cert.encode())?;
            println!("studio {studio_id} certified for up to {max_shops} shops -> {}", out.display());
        }
        Cmd::VendorCapacity { vendor, studio_id, block_id, start, count, out } => {
            let vk = sign_key(&vendor.join("vendor.key"))?;
            let block = Signed::sign(
                CapacityBlock { block_id, studio_id, serial_start: start, serial_count: count, vendor_key_id: vk.public().key_id() },
                &vk,
            );
            fs::write(&out, block.encode())?;
            println!("capacity block {block_id} for studio {studio_id}: serials {start}..{} -> {}", start + count as u64 - 1, out.display());
        }
        Cmd::Capacity { vendor, studio, block_id, start, count } => {
            let vk = sign_key(&vendor.join("vendor.key"))?;
            let sc: Signed<StudioCert> = record(&studio.join("studio.cert"))?;
            let block = Signed::sign(
                CapacityBlock { block_id, studio_id: sc.body.studio_id, serial_start: start, serial_count: count, vendor_key_id: vk.public().key_id() },
                &vk,
            );
            let p = studio.join(format!("capacity-{block_id}.bin"));
            fs::write(&p, block.encode())?;
            println!("capacity block {block_id}: serials {start}..{} -> {}", start + count as u64 - 1, p.display());
        }
        Cmd::ShopInit { studio, dir, shop_id } => {
            fs::create_dir_all(dir.join("titles"))?;
            let sk = sign_key(&studio.join("studio.key"))?;
            let sc: Signed<StudioCert> = record(&studio.join("studio.cert"))?;
            let sign = SignKey::generate();
            let kex = KexSecret::generate();
            let cert = Signed::sign(
                ShopCert { shop_id, studio_id: sc.body.studio_id, sign_key: sign.public(), kex_key: kex.public() },
                &sk,
            );
            let mac: [u8; 32] = mesob_core::keys::random();
            write_hex(&dir.join("shop.sign.key"), &sign.to_bytes())?;
            write_hex(&dir.join("shop.kex.key"), &kex.to_bytes())?;
            write_hex(&dir.join("shop.mac"), &mac)?;
            fs::write(dir.join("shop.cert"), cert.encode())?;
            fs::copy(studio.join("studio.cert"), dir.join("studio.cert"))?;
            fs::copy(studio.join("vendor.pub"), dir.join("vendor.pub"))?;
            println!("shop {shop_id} set up in {}", dir.display());
        }
        Cmd::Credit { studio, shop, block, pack_id, start, count } => {
            let sk = sign_key(&studio.join("studio.key"))?;
            let sc: Signed<StudioCert> = record(&studio.join("studio.cert"))?;
            let shop_cert: Signed<ShopCert> = record(&shop.join("shop.cert"))?;
            let cap: Signed<CapacityBlock> = record(&block)?;
            if !mesob_core::records::range_within(cap.body.serial_start, cap.body.serial_count, start, count) {
                return Err("credit pack must sit inside the capacity block".into());
            }
            let pack = Signed::sign(
                CreditPack { pack_id, shop_id: shop_cert.body.shop_id, studio_id: sc.body.studio_id, block_id: cap.body.block_id, serial_start: start, serial_count: count },
                &sk,
            );
            let p = shop.join(format!("pack-{pack_id}.bin"));
            fs::write(&p, pack.encode())?;
            println!("credit pack {pack_id} for shop {}: {count} licenses -> {}", shop_cert.body.shop_id, p.display());
        }
        Cmd::Package { studio, input, out, kind, title_en, title_ti, artist, poster } => {
            let sk = sign_key(&studio.join("studio.key"))?;
            let sc: Signed<StudioCert> = record(&studio.join("studio.cert"))?;
            let (kind, mime) = match kind.as_str() {
                "video" => (Kind::Video, "video/mp4"),
                "audio" => (Kind::Audio, "audio/mp4"),
                "pdf" => (Kind::Pdf, "application/pdf"),
                _ => return Err("kind must be video, audio or pdf".into()),
            };
            let poster = match poster {
                Some(p) => fs::read(p)?,
                None => vec![],
            };
            let meta = TitleMeta { kind, mime: mime.into(), title_en, title_ti, artist, poster };
            let mut inp = BufReader::new(File::open(&input)?);
            let mut o = BufWriter::new(File::create(&out)?);
            let p = package(&mut inp, &mut o, &sc, &sk, meta, DEFAULT_CHUNK)?;
            write_hex(&studio.join("keys").join(format!("{}.key", hex::encode(p.title_id))), &p.title_key)?;
            println!("title {} ({} bytes) -> {}", hex::encode(p.title_id), p.plain_size, out.display());
        }
        Cmd::Grant { studio, shop, title } => {
            let t = open_title(&title, &vendor_of(&studio)?)?;
            let id = t.title_id();
            let key = read_key32(&studio.join("keys").join(format!("{}.key", hex::encode(id))))?;
            let shop_cert: Signed<ShopCert> = record(&shop.join("shop.cert"))?;
            let sealed = seal_to_shop(&shop_cert.body.kex_key, &key, &shop_ctx(&id))?;
            fs::write(shop.join("titles").join(format!("{}.sealed", hex::encode(id))), sealed)?;
            println!("shop {} can now sell title {}", shop_cert.body.shop_id, hex::encode(id));
        }
        Cmd::DeviceInit { dir } => {
            fs::create_dir_all(&dir)?;
            let d = DeviceSecret::generate();
            write_hex(&dir.join("device.key"), &d.to_bytes())?;
            write_hex(&dir.join("device.pub"), &d.public())?;
            println!("device {}", hex::encode(d.fingerprint()));
        }
        Cmd::Sell { studio, shop, block, pack, title, device_pub, serial, price, out } => {
            let vendor = vendor_of(&studio)?;
            let t = open_title(&title, &vendor)?;
            let id = t.title_id();
            let kex = KexSecret::from_bytes(&read_key32(&shop.join("shop.kex.key"))?)?;
            let sealed = fs::read(shop.join("titles").join(format!("{}.sealed", hex::encode(id))))
                .map_err(|_| "this shop has not been granted that title")?;
            let title_key = open_at_shop(&kex, &sealed, &shop_ctx(&id))?;
            let sign = sign_key(&shop.join("shop.sign.key"))?;
            let shop_cert: Signed<ShopCert> = record(&shop.join("shop.cert"))?;
            let pack_rec: Signed<CreditPack> = record(&pack)?;
            let dev: [u8; 32] = read_key32(&device_pub)?;
            let mut entries = load_log(&shop)?;
            if entries.iter().any(|e| e.body.serial == serial) {
                return Err(format!("serial {serial} was already used by this shop").into());
            }
            let lic = issue_license(&sign, &shop_cert, &pack_rec, serial, id, &title_key, &dev, 0)?;
            let bundle = LicenseBundle {
                studio: record(&studio.join("studio.cert"))?,
                capacity: record(&block)?,
                shop: shop_cert,
                pack: pack_rec,
                license: lic,
            };
            bundle.verify(&vendor, &device_fingerprint(&dev))?;
            let bytes = bundle.encode();
            fs::write(&out, &bytes)?;
            let e = log::append(entries.last(), &sign, log::KIND_SALE, serial, id, device_fingerprint(&dev), price, 0);
            entries.push(e);
            save_log(&shop, &entries)?;
            println!("license serial {serial} ({} bytes) -> {}", bytes.len(), out.display());
        }
        Cmd::Inspect { vendor, file } => {
            let vendor = pub_key(&vendor)?;
            let mut f = File::open(&file)?;
            let mut magic = [0u8; 4];
            f.read_exact(&mut magic)?;
            f.seek(SeekFrom::Start(0))?;
            if &magic == mesob_core::title::MAGIC {
                let mut t = TitleFile::open(BufReader::new(f), &vendor)?;
                let h = &t.header.body;
                println!("title     {}", hex::encode(h.title_id));
                println!("studio    {}", h.studio_id);
                println!("kind      {:?} ({})", h.kind, h.mime);
                println!("name      {} / {}", h.title_en, h.title_ti);
                println!("artist    {}", h.artist);
                println!("size      {} bytes in {} chunks", h.plain_size, h.chunk_hashes.len());
                t.check_integrity()?;
                println!("signatures and all chunk fingerprints OK");
            } else {
                let b = LicenseBundle::decode(&fs::read(&file)?)?;
                let l = &b.license.body;
                println!("license serial {} for title {}", l.serial, hex::encode(l.title_id));
                println!("device  {}", hex::encode(l.device_fp));
                println!("shop    {} pack {} (block {})", l.shop_id, l.pack_id, b.capacity.body.block_id);
                b.verify(&vendor, &l.device_fp)?;
                println!("chain OK back to vendor key {}", hex::encode(vendor.key_id()));
            }
        }
        Cmd::Unlock { vendor, device, license, title, out } => {
            let vendor = pub_key(&vendor)?;
            let dev = DeviceSecret::from_bytes(read_key32(&device.join("device.key"))?);
            let b = LicenseBundle::decode(&fs::read(&license)?)?;
            let t = open_title(&title, &vendor)?;
            let mut r = b.open_title(t, &vendor, &dev)?;
            let mut o = BufWriter::new(File::create(&out)?);
            let n = std::io::copy(&mut r, &mut o)?;
            println!("decrypted {n} bytes -> {} (lab only)", out.display());
        }
        Cmd::Code(c) => match c {
            CodeCmd::Unlock { shop, pack_id } => {
                let cert: Signed<ShopCert> = record(&shop.join("shop.cert"))?;
                println!("{}", codes::unlock_code(&read_key32(&shop.join("shop.mac"))?, cert.body.shop_id, pack_id));
            }
            CodeCmd::ReadUnlock { shop, code } => {
                let cert: Signed<ShopCert> = record(&shop.join("shop.cert"))?;
                let pack = codes::read_unlock(&read_key32(&shop.join("shop.mac"))?, cert.body.shop_id, &code)?;
                println!("unlocks credit pack {pack}");
            }
            CodeCmd::Report { shop, day } => {
                let cert: Signed<ShopCert> = record(&shop.join("shop.cert"))?;
                let entries = load_log(&shop)?;
                let s = verify_chain(&entries, &cert.body.sign_key, 0, [0u8; 32])?;
                let r = Report { day, count: s.sales as u16, cash: s.cash as u32, log_tag: s.last_hash[..3].try_into()? };
                println!("{}", codes::report_code(&read_key32(&shop.join("shop.mac"))?, cert.body.shop_id, &r));
            }
            CodeCmd::ReadReport { shop, code } => {
                let cert: Signed<ShopCert> = record(&shop.join("shop.cert"))?;
                let r = codes::read_report(&read_key32(&shop.join("shop.mac"))?, cert.body.shop_id, &code)?;
                println!("day {} · {} sales · {} Nakfa · log {}", r.day, r.count, r.cash, hex::encode(r.log_tag));
            }
        },
        Cmd::VerifyLog { shop } => {
            let cert: Signed<ShopCert> = record(&shop.join("shop.cert"))?;
            let s = verify_chain(&load_log(&shop)?, &cert.body.sign_key, 0, [0u8; 32])?;
            println!("{} entries: {} sales, {} re-issues, {} Nakfa; chain OK", s.entries, s.sales, s.reissues, s.cash);
        }
    }
    Ok(())
}

fn main() {
    if let Err(e) = run(Cli::parse()) {
        eprintln!("error: {e}");
        std::process::exit(1);
    }
}
