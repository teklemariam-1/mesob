// A production core has the vendor root key compiled in (MESOB_VENDOR_PUB, hex). A development
// core must be asked for explicitly with `--features lab`. Building neither is a compile error, so
// a lab core (which accepts a vendor key at runtime) can't be shipped by accident.
fn main() {
    println!("cargo:rustc-check-cfg=cfg(mesob_vendor)");
    println!("cargo:rerun-if-env-changed=MESOB_VENDOR_PUB");
    if let Ok(v) = std::env::var("MESOB_VENDOR_PUB") {
        let ok = v.len() == 66 && v.chars().all(|c| c.is_ascii_hexdigit());
        if !ok {
            panic!("MESOB_VENDOR_PUB must be the 33-byte vendor public key as 66 hex characters");
        }
        println!("cargo:rustc-cfg=mesob_vendor");
    }
}
