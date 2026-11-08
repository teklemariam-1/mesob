//! The whole system through the app-facing JSON API: vendor → studio → shop → player.

use base64::engine::general_purpose::STANDARD as B64;
use base64::Engine;
use mesob::{dispatch, session_close, session_read, session_size};
use serde_json::{json, Value};

fn call(req: Value) -> Value {
    let out: Value = serde_json::from_str(&dispatch(&req.to_string())).unwrap();
    assert_eq!(out["ok"], true, "{req} -> {out}");
    out["result"].clone()
}

fn fails(req: Value) -> String {
    let out: Value = serde_json::from_str(&dispatch(&req.to_string())).unwrap();
    assert_eq!(out["ok"], false, "{req} should fail");
    out["error"].as_str().unwrap().to_string()
}

#[test]
fn full_flow_through_the_bridge() {
    let dir = std::env::temp_dir().join(format!("mesob-ffi-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let info = call(json!({"op": "core.info"}));
    assert_eq!(info["lab"], true, "tests run as a lab build");

    // Vendor and studio
    let vendor = call(json!({"op": "studio.keygen"}));
    let vendor_pub = hex::encode(B64.decode(vendor["public"].as_str().unwrap()).unwrap());
    let studio = call(json!({"op": "studio.keygen"}));
    let sc = call(json!({"op": "vendor.studio_cert", "vendor_secret": vendor["secret"], "studio_id": 1, "studio_public": studio["public"], "max_shops": 5}))["cert"].clone();
    let cap = call(json!({"op": "vendor.capacity", "vendor_secret": vendor["secret"], "block_id": 1, "studio_id": 1, "serial_start": 1000, "serial_count": 100}))["capacity"].clone();

    // Shop setup
    let ss = call(json!({"op": "studio.keygen"}));
    let sk = call(json!({"op": "studio.kexgen"}));
    let shop_cert = call(json!({"op": "studio.shop_cert", "studio_secret": studio["secret"], "studio_cert": sc, "shop_id": 2, "sign_public": ss["public"], "kex_public": sk["public"]}))["cert"].clone();
    assert!(fails(json!({"op": "studio.shop_cert", "studio_secret": studio["secret"], "studio_cert": sc, "shop_id": 6, "sign_public": ss["public"], "kex_public": sk["public"]})).contains("shops 1 to 5"));
    let pack = call(json!({"op": "studio.credit_pack", "vendor_pub": vendor_pub, "studio_secret": studio["secret"], "studio_cert": sc, "capacity": cap, "pack_id": 1, "shop_id": 2, "serial_start": 1010, "serial_count": 10}))["pack"].clone();
    assert!(fails(json!({"op": "studio.credit_pack", "vendor_pub": vendor_pub, "studio_secret": studio["secret"], "studio_cert": sc, "capacity": cap, "pack_id": 2, "shop_id": 2, "serial_start": 1095, "serial_count": 10})).contains("inside the capacity"));

    // Package a "video"
    let input = dir.join("in.mp4");
    let plain: Vec<u8> = (0..300_000u32).map(|i| (i % 253) as u8).collect();
    std::fs::write(&input, &plain).unwrap();
    let output = dir.join("film.msb");
    let pk = call(json!({"op": "studio.package", "studio_secret": studio["secret"], "studio_cert": sc, "kind": "video", "mime": "video/mp4", "title_en": "Film", "title_ti": "ፊልም", "input": input.to_str(), "output": output.to_str()}));
    let sealed = call(json!({"op": "studio.grant", "title_id": pk["title_id"], "title_key": pk["title_key"], "kex_public": sk["public"]}))["sealed"].clone();
    let inspected = call(json!({"op": "title.inspect", "vendor_pub": vendor_pub, "path": output.to_str(), "check": true}));
    assert_eq!(inspected["title_ti"], "ፊልም");

    // Customer device asks for the title, shop sells it
    let dev = call(json!({"op": "device.create"}));
    let request = call(json!({"op": "request.make", "device_secret": dev["secret"], "title_ids": [pk["title_id"]]}))["request"].clone();
    let parsed = call(json!({"op": "request.parse", "request": request}));
    assert_eq!(parsed["device"], dev["fingerprint"]);
    let sold = call(json!({"op": "shop.issue", "vendor_pub": vendor_pub, "sign_secret": ss["secret"], "kex_secret": sk["secret"], "shop_cert": shop_cert, "pack": pack, "studio_cert": sc, "capacity": cap, "sealed_title_key": sealed, "title_id": pk["title_id"], "device_public": parsed["device_public"], "serial": 1010}));
    assert!(fails(json!({"op": "shop.issue", "vendor_pub": vendor_pub, "sign_secret": ss["secret"], "kex_secret": sk["secret"], "shop_cert": shop_cert, "pack": pack, "studio_cert": sc, "capacity": cap, "sealed_title_key": sealed, "title_id": pk["title_id"], "device_public": parsed["device_public"], "serial": 1020})).contains("outside credit pack"));

    // Sales log + codes
    let e1 = call(json!({"op": "log.append", "sign_secret": ss["secret"], "kind": "sale", "serial": 1010, "title_id": pk["title_id"], "device": dev["fingerprint"], "price": 150}));
    let sum = call(json!({"op": "log.verify", "shop_cert": shop_cert, "entries": [e1["entry"]]}));
    assert_eq!(sum["cash"], 150);
    let secret = call(json!({"op": "studio.secret32"}))["secret"].clone();
    let code = call(json!({"op": "code.unlock", "secret": secret, "shop_id": 2, "pack_id": 9}))["code"].clone();
    assert_eq!(call(json!({"op": "code.read_unlock", "secret": secret, "shop_id": 2, "code": code}))["pack_id"], 9);

    // Player plays it
    let lic = call(json!({"op": "license.verify", "vendor_pub": vendor_pub, "bundle": sold["bundle"], "device_secret": dev["secret"]}));
    assert_eq!(lic["serial"], 1010);
    let s = call(json!({"op": "session.open", "vendor_pub": vendor_pub, "bundle": sold["bundle"], "device_secret": dev["secret"], "path": output.to_str()}));
    let h = s["handle"].as_u64().unwrap();
    assert_eq!(session_size(h), plain.len() as i64);
    let mut buf = vec![0u8; 4096];
    let n = session_read(h, 123_456, &mut buf);
    assert_eq!(&buf[..n as usize], &plain[123_456..123_456 + n as usize]);
    session_close(h);
    assert_eq!(session_read(h, 0, &mut buf), -1);

    // Another device can't open it
    let other = call(json!({"op": "device.create"}));
    assert!(fails(json!({"op": "session.open", "vendor_pub": vendor_pub, "bundle": sold["bundle"], "device_secret": other["secret"], "path": output.to_str()})).contains("another device"));
    std::fs::remove_dir_all(&dir).ok();
}
