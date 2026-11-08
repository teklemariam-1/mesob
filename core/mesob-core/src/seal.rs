//! Sealing a 32-byte title key to one recipient.
//!
//! One-time ECDH key → HKDF-SHA256 → AES-256-GCM. The context bytes (title id, serial) are bound
//! into the derived key, so a sealed key can't be moved to another license.
//!
//! * to a customer device: X25519, 80 bytes (32 one-time public key + 48 ciphertext and tag)
//! * to a shop: P-256, 81 bytes (33 one-time public key + 48), for Android Keystore ECDH

use crate::keys::{DeviceSecret, KexSecret, PubKey};
use crate::{Error, Result};
use aes_gcm::aead::{Aead, KeyInit};
use aes_gcm::{Aes256Gcm, Nonce};
use hkdf::Hkdf;
use p256::elliptic_curve::sec1::ToEncodedPoint;
use rand_core::OsRng;
use sha2::Sha256;
use zeroize::Zeroize;

pub const SEALED_DEVICE_LEN: usize = 32 + 48;
pub const SEALED_SHOP_LEN: usize = 33 + 48;

fn derive(shared: &[u8], eph: &[u8], recipient: &[u8], ctx: &[u8]) -> ([u8; 32], [u8; 12]) {
    let salt = [eph, recipient].concat();
    let hk = Hkdf::<Sha256>::new(Some(&salt), shared);
    let info = [b"mesob/seal/v1\0".as_slice(), ctx].concat();
    let mut okm = [0u8; 44];
    hk.expand(&info, &mut okm).expect("44 bytes is a valid HKDF length");
    let mut k = [0u8; 32];
    let mut n = [0u8; 12];
    k.copy_from_slice(&okm[..32]);
    n.copy_from_slice(&okm[32..]);
    okm.zeroize();
    (k, n)
}

fn aead_seal(mut k: [u8; 32], n: [u8; 12], key: &[u8; 32]) -> Vec<u8> {
    let ct = Aes256Gcm::new_from_slice(&k)
        .expect("32-byte key")
        .encrypt(Nonce::from_slice(&n), key.as_slice())
        .expect("AES-GCM encryption cannot fail for 32 bytes");
    k.zeroize();
    ct
}

fn aead_open(mut k: [u8; 32], n: [u8; 12], ct: &[u8]) -> Result<[u8; 32]> {
    let pt = Aes256Gcm::new_from_slice(&k)
        .expect("32-byte key")
        .decrypt(Nonce::from_slice(&n), ct)
        .map_err(|_| Error::Decrypt);
    k.zeroize();
    let mut pt = pt?;
    let out: [u8; 32] = pt.as_slice().try_into().map_err(|_| Error::Decrypt)?;
    pt.zeroize();
    Ok(out)
}

/// Context that binds a sealed key to one license.
pub fn license_ctx(title_id: &[u8; 16], serial: u64) -> Vec<u8> {
    [b"license".as_slice(), title_id, &serial.to_be_bytes()].concat()
}

/// Context that binds a sealed key to one title delivered to a shop.
pub fn shop_ctx(title_id: &[u8; 16]) -> Vec<u8> {
    [b"shop-title".as_slice(), title_id].concat()
}

pub fn seal_to_device(device_pub: &[u8; 32], key: &[u8; 32], ctx: &[u8]) -> Result<[u8; SEALED_DEVICE_LEN]> {
    let eph = x25519_dalek::EphemeralSecret::random_from_rng(OsRng);
    let eph_pub = x25519_dalek::PublicKey::from(&eph);
    let shared = eph.diffie_hellman(&x25519_dalek::PublicKey::from(*device_pub));
    if !shared.was_contributory() {
        return Err(Error::Malformed("device public key"));
    }
    let (k, n) = derive(shared.as_bytes(), eph_pub.as_bytes(), device_pub, ctx);
    let ct = aead_seal(k, n, key);
    let mut out = [0u8; SEALED_DEVICE_LEN];
    out[..32].copy_from_slice(eph_pub.as_bytes());
    out[32..].copy_from_slice(&ct);
    Ok(out)
}

pub fn open_on_device(device: &DeviceSecret, sealed: &[u8; SEALED_DEVICE_LEN], ctx: &[u8]) -> Result<[u8; 32]> {
    let mut eph = [0u8; 32];
    eph.copy_from_slice(&sealed[..32]);
    let shared = device.inner().diffie_hellman(&x25519_dalek::PublicKey::from(eph));
    if !shared.was_contributory() {
        return Err(Error::Decrypt);
    }
    let (k, n) = derive(shared.as_bytes(), &eph, &device.public(), ctx);
    aead_open(k, n, &sealed[32..])
}

pub fn seal_to_shop(shop_kex: &PubKey, key: &[u8; 32], ctx: &[u8]) -> Result<Vec<u8>> {
    let recipient = p256::PublicKey::from_sec1_bytes(&shop_kex.0).map_err(|_| Error::Malformed("shop key"))?;
    let eph = p256::ecdh::EphemeralSecret::random(&mut OsRng);
    let eph_pub = eph.public_key().to_encoded_point(true);
    let shared = eph.diffie_hellman(&recipient);
    let (k, n) = derive(shared.raw_secret_bytes().as_slice(), eph_pub.as_bytes(), &shop_kex.0, ctx);
    let ct = aead_seal(k, n, key);
    let mut out = Vec::with_capacity(SEALED_SHOP_LEN);
    out.extend_from_slice(eph_pub.as_bytes());
    out.extend_from_slice(&ct);
    Ok(out)
}

pub fn open_at_shop(shop: &KexSecret, sealed: &[u8], ctx: &[u8]) -> Result<[u8; 32]> {
    if sealed.len() != SEALED_SHOP_LEN {
        return Err(Error::Malformed("sealed shop key"));
    }
    let eph = p256::PublicKey::from_sec1_bytes(&sealed[..33]).map_err(|_| Error::Decrypt)?;
    let shared = p256::ecdh::diffie_hellman(shop.inner().to_nonzero_scalar(), eph.as_affine());
    open_at_shop_shared(shared.raw_secret_bytes().as_slice(), sealed, &shop.public(), ctx)
}

/// The one-time public key inside a sealed shop key, for hardware ECDH (Android Keystore).
pub fn sealed_ephemeral(sealed: &[u8]) -> Result<PubKey> {
    if sealed.len() != SEALED_SHOP_LEN {
        return Err(Error::Malformed("sealed shop key"));
    }
    PubKey::from_slice(&sealed[..33])
}

/// Open with an ECDH shared secret computed by hardware (the X coordinate, 32 bytes).
pub fn open_at_shop_shared(shared: &[u8], sealed: &[u8], shop_kex: &PubKey, ctx: &[u8]) -> Result<[u8; 32]> {
    if sealed.len() != SEALED_SHOP_LEN || shared.len() != 32 {
        return Err(Error::Malformed("sealed shop key"));
    }
    let (k, n) = derive(shared, &sealed[..33], &shop_kex.0, ctx);
    aead_open(k, n, &sealed[33..])
}
