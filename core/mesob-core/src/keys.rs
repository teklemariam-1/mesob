//! Key types.
//!
//! * Signing keys (vendor, studio, shop) are ECDSA P-256, because TPM 2.0, Android Keystore and
//!   hardware security keys all support it. In production these keys stay in that hardware;
//!   `SignKey` here is the software form used by the toolkit and tests.
//! * Device keys are X25519, stored by each app locked under a hardware Keystore/TPM key.
//! * Shop key-exchange keys are P-256 (ECDH), so a shop tablet can hold them in Android Keystore.

use crate::{Error, Result};
use p256::ecdsa::signature::{Signer, Verifier};
use p256::ecdsa::{Signature, SigningKey, VerifyingKey};
use p256::elliptic_curve::sec1::ToEncodedPoint;
use rand_core::{OsRng, RngCore};
use sha2::{Digest, Sha256};

pub const PUB_LEN: usize = 33;
pub const SIG_LEN: usize = 64;

pub fn sha256(parts: &[&[u8]]) -> [u8; 32] {
    let mut h = Sha256::new();
    for p in parts {
        h.update(p);
    }
    h.finalize().into()
}

pub fn random<const N: usize>() -> [u8; N] {
    let mut a = [0u8; N];
    OsRng.fill_bytes(&mut a);
    a
}

/// Domain separation: a signature made for one record type can never verify as another.
/// Hardware signers (Android Keystore, TPM) sign exactly these bytes with SHA256withECDSA.
pub fn tagged(domain: &str, msg: &[u8]) -> Vec<u8> {
    let mut v = Vec::with_capacity(domain.len() + 1 + msg.len());
    v.extend_from_slice(domain.as_bytes());
    v.push(0);
    v.extend_from_slice(msg);
    v
}

/// A signature from a hardware signer: DER (Java, Windows CNG) or raw r||s, as 64 raw bytes.
pub fn signature_bytes(sig: &[u8]) -> Result<[u8; SIG_LEN]> {
    let s = if sig.len() == SIG_LEN { Signature::from_slice(sig) } else { Signature::from_der(sig) }
        .map_err(|_| Error::Malformed("signature"))?;
    let mut out = [0u8; SIG_LEN];
    out.copy_from_slice(&s.to_bytes());
    Ok(out)
}

/// A P-256 public key in SEC1 compressed form (33 bytes).
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct PubKey(pub [u8; PUB_LEN]);

impl PubKey {
    /// From an X.509 SubjectPublicKeyInfo (what Android Keystore and Windows CNG export).
    pub fn from_spki(der: &[u8]) -> Result<Self> {
        use p256::pkcs8::DecodePublicKey;
        let pk = p256::PublicKey::from_public_key_der(der).map_err(|_| Error::Malformed("public key"))?;
        Ok(PubKey(pk.to_encoded_point(true).as_bytes().try_into().expect("33 bytes")))
    }

    /// As X.509 SubjectPublicKeyInfo DER (to hand a key to Java or CNG).
    pub fn to_spki(&self) -> Result<Vec<u8>> {
        use p256::pkcs8::EncodePublicKey;
        let pk = p256::PublicKey::from_sec1_bytes(&self.0).map_err(|_| Error::Malformed("public key"))?;
        Ok(pk.to_public_key_der().map_err(|_| Error::Malformed("public key"))?.as_bytes().to_vec())
    }

    pub fn from_slice(b: &[u8]) -> Result<Self> {
        let a: [u8; PUB_LEN] = b.try_into().map_err(|_| Error::Malformed("public key"))?;
        VerifyingKey::from_sec1_bytes(&a).map_err(|_| Error::Malformed("public key"))?;
        Ok(PubKey(a))
    }

    /// Short identifier carried in records that name their issuer.
    pub fn key_id(&self) -> [u8; 8] {
        let h = sha256(&[b"mesob/key-id/v1", &self.0]);
        h[..8].try_into().expect("8 bytes")
    }

    pub fn verify(&self, domain: &'static str, msg: &[u8], sig: &[u8; SIG_LEN]) -> Result<()> {
        let vk = VerifyingKey::from_sec1_bytes(&self.0).map_err(|_| Error::Malformed("public key"))?;
        let s = Signature::from_slice(sig).map_err(|_| Error::BadSignature(domain))?;
        vk.verify(&tagged(domain, msg), &s).map_err(|_| Error::BadSignature(domain))
    }
}

/// Software P-256 signing key (toolkit and tests; production keys stay in hardware).
pub struct SignKey(SigningKey);

impl SignKey {
    pub fn generate() -> Self {
        SignKey(SigningKey::random(&mut OsRng))
    }
    pub fn from_bytes(b: &[u8; 32]) -> Result<Self> {
        SigningKey::from_slice(b).map(SignKey).map_err(|_| Error::Malformed("signing key"))
    }
    pub fn to_bytes(&self) -> [u8; 32] {
        let mut out = [0u8; 32];
        out.copy_from_slice(&self.0.to_bytes());
        out
    }
    pub fn public(&self) -> PubKey {
        let ep = self.0.verifying_key().to_encoded_point(true);
        PubKey(ep.as_bytes().try_into().expect("compressed point is 33 bytes"))
    }
    pub fn sign(&self, domain: &'static str, msg: &[u8]) -> [u8; SIG_LEN] {
        let s: Signature = self.0.sign(&tagged(domain, msg));
        let mut out = [0u8; SIG_LEN];
        out.copy_from_slice(&s.to_bytes());
        out
    }
}

/// A customer device's identity key (X25519).
pub struct DeviceSecret(x25519_dalek::StaticSecret);

impl DeviceSecret {
    pub fn generate() -> Self {
        DeviceSecret(x25519_dalek::StaticSecret::random_from_rng(OsRng))
    }
    pub fn from_bytes(b: [u8; 32]) -> Self {
        DeviceSecret(x25519_dalek::StaticSecret::from(b))
    }
    pub fn to_bytes(&self) -> [u8; 32] {
        self.0.to_bytes()
    }
    pub fn public(&self) -> [u8; 32] {
        x25519_dalek::PublicKey::from(&self.0).to_bytes()
    }
    pub fn fingerprint(&self) -> [u8; 16] {
        device_fingerprint(&self.public())
    }
    pub(crate) fn inner(&self) -> &x25519_dalek::StaticSecret {
        &self.0
    }
}

/// The 16-byte device identifier that licenses are bound to.
pub fn device_fingerprint(public: &[u8; 32]) -> [u8; 16] {
    sha256(&[b"mesob/device/v1", public])[..16].try_into().expect("16 bytes")
}

/// A shop's key-exchange key (P-256 ECDH); title keys are sealed to it.
pub struct KexSecret(p256::SecretKey);

impl KexSecret {
    pub fn generate() -> Self {
        KexSecret(p256::SecretKey::random(&mut OsRng))
    }
    pub fn from_bytes(b: &[u8; 32]) -> Result<Self> {
        p256::SecretKey::from_slice(b).map(KexSecret).map_err(|_| Error::Malformed("key-exchange key"))
    }
    pub fn to_bytes(&self) -> [u8; 32] {
        let mut out = [0u8; 32];
        out.copy_from_slice(&self.0.to_bytes());
        out
    }
    pub fn public(&self) -> PubKey {
        let ep = self.0.public_key().to_encoded_point(true);
        PubKey(ep.as_bytes().try_into().expect("compressed point is 33 bytes"))
    }
    pub(crate) fn inner(&self) -> &p256::SecretKey {
        &self.0
    }
}
