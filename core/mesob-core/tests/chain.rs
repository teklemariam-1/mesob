//! End-to-end checks of the protection chain, including the attacks it must stop.

use mesob_core::bundle::{issue_license, LicenseBundle};
use mesob_core::codes::{self, Report};
use mesob_core::keys::{DeviceSecret, KexSecret, SignKey};
use mesob_core::log::{self, verify_chain};
use mesob_core::records::{CapacityBlock, CreditPack, ShopCert, Signed, StudioCert};
use mesob_core::seal::{open_at_shop, seal_to_shop, shop_ctx};
use mesob_core::title::{package, Kind, TitleFile, TitleMeta};
use mesob_core::Error;
use std::io::{Cursor, Read, Seek, SeekFrom};

struct World {
    vendor: SignKey,
    studio_key: SignKey,
    studio: Signed<StudioCert>,
    capacity: Signed<CapacityBlock>,
    shop_key: SignKey,
    shop_kex: KexSecret,
    shop: Signed<ShopCert>,
    pack: Signed<CreditPack>,
    file: Vec<u8>,
    plain: Vec<u8>,
    title_id: [u8; 16],
    shop_sealed_key: Vec<u8>,
}

fn sample(n: usize) -> Vec<u8> {
    (0..n).map(|i| (i * 31 % 251) as u8).collect()
}

fn world() -> World {
    let vendor = SignKey::generate();
    let studio_key = SignKey::generate();
    let studio = Signed::sign(
        StudioCert { studio_id: 1, studio_key: studio_key.public(), max_shops: 20, features: 0, vendor_key_id: vendor.public().key_id() },
        &vendor,
    );
    let capacity = Signed::sign(
        CapacityBlock { block_id: 1, studio_id: 1, serial_start: 1_000_000, serial_count: 50_000, vendor_key_id: vendor.public().key_id() },
        &vendor,
    );
    let shop_key = SignKey::generate();
    let shop_kex = KexSecret::generate();
    let shop = Signed::sign(
        ShopCert { shop_id: 7, studio_id: 1, sign_key: shop_key.public(), kex_key: shop_kex.public() },
        &studio_key,
    );
    let pack = Signed::sign(
        CreditPack { pack_id: 3, shop_id: 7, studio_id: 1, block_id: 1, serial_start: 1_004_001, serial_count: 500 },
        &studio_key,
    );
    let plain = sample(2_621_440 + 12_345); // 2.5 MiB + a partial chunk
    let mut file = Vec::new();
    let meta = TitleMeta {
        kind: Kind::Video,
        mime: "video/mp4".into(),
        title_en: "Test film".into(),
        title_ti: "ፈተና".into(),
        artist: "Studio".into(),
        poster: vec![1, 2, 3],
    };
    let p = package(&mut Cursor::new(plain.clone()), &mut file, &studio, &studio_key, meta, 1 << 20).unwrap();
    let shop_sealed_key = seal_to_shop(&shop.body.kex_key, &p.title_key, &shop_ctx(&p.title_id)).unwrap();
    World { vendor, studio_key, studio, capacity, shop_key, shop_kex, shop, pack, file, plain, title_id: p.title_id, shop_sealed_key }
}

fn sell(w: &World, device: &DeviceSecret, serial: u64) -> LicenseBundle {
    let key = open_at_shop(&w.shop_kex, &w.shop_sealed_key, &shop_ctx(&w.title_id)).unwrap();
    let license = issue_license(&w.shop_key, &w.shop, &w.pack, serial, w.title_id, &key, &device.public(), 0).unwrap();
    LicenseBundle { studio: w.studio.clone(), capacity: w.capacity.clone(), shop: w.shop.clone(), pack: w.pack.clone(), license }
}

fn open(w: &World, file: &[u8]) -> mesob_core::Result<TitleFile<Cursor<Vec<u8>>>> {
    TitleFile::open(Cursor::new(file.to_vec()), &w.vendor.public())
}

#[test]
fn bought_title_plays_with_random_seeks() {
    let w = world();
    let dev = DeviceSecret::generate();
    let b = LicenseBundle::decode(&sell(&w, &dev, 1_004_001).encode()).unwrap();
    assert!(b.encode().len() < 700, "bundle must fit one QR code");
    let mut r = b.open_title(open(&w, &w.file).unwrap(), &w.vendor.public(), &dev).unwrap();
    let mut all = Vec::new();
    r.read_to_end(&mut all).unwrap();
    assert_eq!(all, w.plain);
    for &off in &[0u64, 1, 15, 16, 17, 1_048_575, 1_048_576, 2_000_003, w.plain.len() as u64 - 7] {
        r.seek(SeekFrom::Start(off)).unwrap();
        let mut buf = [0u8; 7];
        r.read_exact(&mut buf).unwrap();
        assert_eq!(&buf[..], &w.plain[off as usize..off as usize + 7]);
    }
}

#[test]
fn file_alone_does_not_contain_plaintext() {
    let w = world();
    let needle = &w.plain[100_000..100_064];
    assert!(!w.file.windows(64).any(|win| win == needle));
}

#[test]
fn shared_file_does_not_play_on_another_device() {
    let w = world();
    let buyer = DeviceSecret::generate();
    let friend = DeviceSecret::generate();
    let b = sell(&w, &buyer, 1_004_002);
    // Friend gets both the file and the buyer's license (e.g. via Xender).
    let err = b.open_title(open(&w, &w.file).unwrap(), &w.vendor.public(), &friend).err().unwrap();
    assert!(matches!(err, Error::Mismatch(_)), "{err}");
    // Even forcing past the fingerprint check, the sealed key won't open with another device key.
    let ctx = mesob_core::seal::license_ctx(&w.title_id, 1_004_002);
    assert!(mesob_core::seal::open_on_device(&friend, &b.license.body.sealed_key, &ctx).is_err());
}

#[test]
fn edited_license_is_rejected() {
    let w = world();
    let dev = DeviceSecret::generate();
    let mut b = sell(&w, &dev, 1_004_003);
    b.license.body.serial = 1_004_004;
    assert!(matches!(b.verify(&w.vendor.public(), &dev.fingerprint()), Err(Error::BadSignature(_))));
}

#[test]
fn license_for_one_title_does_not_open_another() {
    let w = world();
    let dev = DeviceSecret::generate();
    let b = sell(&w, &dev, 1_004_005);
    let mut other = Vec::new();
    let meta = TitleMeta { kind: Kind::Audio, mime: "audio/mp4".into(), title_en: "Song".into(), title_ti: String::new(), artist: String::new(), poster: vec![] };
    package(&mut Cursor::new(sample(5000)), &mut other, &w.studio, &w.studio_key, meta, 4096).unwrap();
    let err = b.open_title(open(&w, &other).unwrap(), &w.vendor.public(), &dev).err().unwrap();
    assert!(matches!(err, Error::Mismatch(_)), "{err}");
}

#[test]
fn shop_cannot_sell_outside_its_pack() {
    let w = world();
    let dev = DeviceSecret::generate();
    let key = [9u8; 32];
    assert!(issue_license(&w.shop_key, &w.shop, &w.pack, 1_004_501, w.title_id, &key, &dev.public(), 0).is_err());
    // A rogue shop signing a serial outside its pack directly is caught by the player.
    let mut b = sell(&w, &dev, 1_004_006);
    b.license.body.serial = 2_000_000;
    b.license = Signed::sign(b.license.body.clone(), &w.shop_key);
    assert!(matches!(b.verify(&w.vendor.public(), &dev.fingerprint()), Err(Error::OutOfRange(_))));
}

#[test]
fn studio_cannot_exceed_vendor_capacity() {
    let w = world();
    let dev = DeviceSecret::generate();
    let mut b = sell(&w, &dev, 1_004_007);
    // Studio signs itself a pack beyond the capacity it bought.
    b.pack = Signed::sign(CreditPack { serial_start: 1_049_900, serial_count: 500, ..b.pack.body.clone() }, &w.studio_key);
    assert!(matches!(b.verify(&w.vendor.public(), &dev.fingerprint()), Err(Error::OutOfRange(_))));
    // Studio forges its own capacity block: not signed by the vendor.
    let mut b2 = sell(&w, &dev, 1_004_008);
    b2.capacity = Signed::sign(CapacityBlock { serial_count: 10_000_000, ..b2.capacity.body.clone() }, &w.studio_key);
    assert!(b2.verify(&w.vendor.public(), &dev.fingerprint()).is_err());
}

#[test]
fn pirate_vendor_key_is_rejected() {
    let w = world();
    let pirate = SignKey::generate();
    let dev = DeviceSecret::generate();
    let b = sell(&w, &dev, 1_004_009);
    assert!(b.verify(&pirate.public(), &dev.fingerprint()).is_err());
    assert!(open(&w, &w.file).is_ok());
    assert!(TitleFile::open(Cursor::new(w.file.clone()), &pirate.public()).is_err());
}

#[test]
fn shop_beyond_studio_limit_is_rejected() {
    let w = world();
    let dev = DeviceSecret::generate();
    let mut b = sell(&w, &dev, 1_004_010);
    b.shop = Signed::sign(ShopCert { shop_id: 21, ..b.shop.body.clone() }, &w.studio_key);
    assert!(b.verify(&w.vendor.public(), &dev.fingerprint()).is_err());
}

#[test]
fn damaged_or_edited_title_file_is_detected() {
    let w = world();
    let mut bad = w.file.clone();
    let n = bad.len();
    bad[n - 10] ^= 0xFF;
    let mut t = open(&w, &bad).unwrap();
    assert!(matches!(t.check_integrity(), Err(Error::Damaged(2))));
    let mut truncated = w.file.clone();
    truncated.truncate(n - 1);
    assert!(open(&w, &truncated).is_err());
    let mut header_edit = w.file.clone();
    let pos = header_edit.windows(9).position(|x| x == b"Test film").unwrap();
    header_edit[pos] = b'X';
    assert!(open(&w, &header_edit).is_err());
}

#[test]
fn codes_round_trip_and_reject_tampering() {
    let secret = [7u8; 32];
    let code = codes::unlock_code(&secret, 7, 42);
    assert_eq!(code.len(), 24, "{code}");
    assert_eq!(codes::read_unlock(&secret, 7, &code).unwrap(), 42);
    assert_eq!(codes::read_unlock(&secret, 7, &code.to_lowercase().replace('-', " ")).unwrap(), 42);
    assert!(codes::read_unlock(&secret, 8, &code).is_err(), "code for another shop");
    let mut chars: Vec<char> = code.chars().collect();
    chars[3] = if chars[3] == 'A' { 'B' } else { 'A' };
    assert!(codes::read_unlock(&secret, 7, &chars.iter().collect::<String>()).is_err());
    let r = Report { day: 311, count: 57, cash: 28_500, log_tag: [1, 2, 3] };
    let rc = codes::report_code(&secret, 7, &r);
    assert_eq!(codes::read_report(&secret, 7, &rc).unwrap(), r);
    assert!(codes::read_unlock(&secret, 7, &rc).is_err());
}

/// The Shop app keeps its keys in Android Keystore: the core prepares the bytes, the hardware signs
/// them (DER, SHA256withECDSA) and computes ECDH; the private keys never reach the core.
#[test]
fn hardware_held_shop_keys_work_end_to_end() {
    use mesob_core::bundle::license_body;
    use mesob_core::keys::PubKey;
    use mesob_core::records::License;
    use mesob_core::seal::{open_at_shop_shared, sealed_ephemeral};
    use p256::ecdsa::signature::Signer;
    use p256::pkcs8::DecodePublicKey;

    let w = world();
    // "Keystore" keys: we only ever export their public halves as SPKI, like Android does.
    let hw_sign = p256::ecdsa::SigningKey::random(&mut rand_core::OsRng);
    let hw_kex = p256::SecretKey::random(&mut rand_core::OsRng);
    use p256::pkcs8::EncodePublicKey;
    let sign_pub = PubKey::from_spki(hw_sign.verifying_key().to_public_key_der().unwrap().as_bytes()).unwrap();
    let kex_pub = PubKey::from_spki(hw_kex.public_key().to_public_key_der().unwrap().as_bytes()).unwrap();
    assert_eq!(PubKey::from_spki(&sign_pub.to_spki().unwrap()).unwrap(), sign_pub);
    let shop = Signed::sign(ShopCert { shop_id: 8, studio_id: 1, sign_key: sign_pub, kex_key: kex_pub }, &w.studio_key);
    let pack = Signed::sign(CreditPack { pack_id: 9, shop_id: 8, studio_id: 1, block_id: 1, serial_start: 1_010_000, serial_count: 10 }, &w.studio_key);

    // Studio seals the title key to the shop's hardware key; the tablet's Keystore does the ECDH.
    let key = open_at_shop(&w.shop_kex, &w.shop_sealed_key, &shop_ctx(&w.title_id)).unwrap();
    let sealed = seal_to_shop(&kex_pub, &key, &shop_ctx(&w.title_id)).unwrap();
    let eph = sealed_ephemeral(&sealed).unwrap();
    let eph_pk = p256::PublicKey::from_public_key_der(&eph.to_spki().unwrap()).unwrap();
    let shared = p256::ecdh::diffie_hellman(hw_kex.to_nonzero_scalar(), eph_pk.as_affine());
    let opened = open_at_shop_shared(shared.raw_secret_bytes().as_slice(), &sealed, &kex_pub, &shop_ctx(&w.title_id)).unwrap();
    assert_eq!(opened, key);

    // License: prepare, sign in "hardware" (DER), finish.
    let dev = DeviceSecret::generate();
    let body = license_body(&shop, &pack, 1_010_000, w.title_id, &opened, &dev.public(), 0).unwrap();
    let (unsigned, msg) = Signed::prepare(&body);
    let der: p256::ecdsa::DerSignature = hw_sign.sign(&msg);
    let license = Signed::<License>::finish(&unsigned, der.as_bytes(), &sign_pub).unwrap();
    let b = LicenseBundle { studio: w.studio.clone(), capacity: w.capacity.clone(), shop, pack, license };
    let mut r = b.open_title(open(&w, &w.file).unwrap(), &w.vendor.public(), &dev).unwrap();
    let mut first = [0u8; 64];
    r.read_exact(&mut first).unwrap();
    assert_eq!(&first[..], &w.plain[..64]);

    // A signature from any other key is refused at finish.
    let other = p256::ecdsa::SigningKey::random(&mut rand_core::OsRng);
    let bad: p256::ecdsa::DerSignature = other.sign(&msg);
    assert!(Signed::<License>::finish(&unsigned, bad.as_bytes(), &sign_pub).is_err());
}

#[test]
fn sales_log_detects_edits_and_gaps() {
    let shop = SignKey::generate();
    let mut entries = Vec::new();
    for i in 0..5u64 {
        let e = log::append(entries.last(), &shop, log::KIND_SALE, 1_004_001 + i, [i as u8; 16], [0; 16], 500, 0);
        entries.push(e);
    }
    let s = verify_chain(&entries, &shop.public(), 0, [0; 32]).unwrap();
    assert_eq!((s.sales, s.cash), (5, 2500));
    let mut edited = entries.clone();
    edited[2].body.price = 1;
    assert!(verify_chain(&edited, &shop.public(), 0, [0; 32]).is_err());
    let mut gap = entries.clone();
    gap.remove(2);
    assert!(verify_chain(&gap, &shop.public(), 0, [0; 32]).is_err());
    // A continuation verifies from the last confirmed point.
    assert!(verify_chain(&entries[3..], &shop.public(), 3, log::entry_hash(&entries[2])).is_ok());
}
