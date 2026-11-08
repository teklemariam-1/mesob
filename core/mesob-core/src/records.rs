//! Signed records: the chain of trust from the vendor root down to a customer's license.
//!
//! ```text
//! vendor ─signs─▶ StudioCert, CapacityBlock
//! studio ─signs─▶ ShopCert, CreditPack, TitleHeader
//! shop   ─signs─▶ License, SaleEntry
//! ```
//!
//! Encoding: `type (u8) | version (u8) | fields... | signature (64)`. The signature covers
//! `domain || 0x00 || type | version | fields`.

use crate::keys::{PubKey, SignKey, PUB_LEN, SIG_LEN};
use crate::seal::SEALED_DEVICE_LEN;
use crate::wire::{R, W};
use crate::{Error, Result};

pub const RECORD_VERSION: u8 = 1;

pub trait Body: Sized {
    const TYPE: u8;
    const DOMAIN: &'static str;
    fn write(&self, w: &mut W);
    fn read(r: &mut R) -> Result<Self>;
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Signed<T> {
    pub body: T,
    pub sig: [u8; SIG_LEN],
}

impl<T: Body> Signed<T> {
    fn body_bytes(body: &T) -> Vec<u8> {
        let mut w = W::new();
        w.u8(T::TYPE);
        w.u8(RECORD_VERSION);
        body.write(&mut w);
        w.0
    }

    pub fn sign(body: T, key: &SignKey) -> Self {
        let sig = key.sign(T::DOMAIN, &Self::body_bytes(&body));
        Signed { body, sig }
    }

    /// For keys held in hardware: the record without its signature, and the exact message the
    /// hardware must sign (SHA256withECDSA).
    pub fn prepare(body: &T) -> (Vec<u8>, Vec<u8>) {
        let b = Self::body_bytes(body);
        let msg = crate::keys::tagged(T::DOMAIN, &b);
        (b, msg)
    }

    /// Attach a hardware signature to a prepared body and check it against `key`.
    pub fn finish(unsigned: &[u8], sig: &[u8], key: &PubKey) -> Result<Self> {
        let mut r = R::new(unsigned);
        if r.u8()? != T::TYPE || r.u8()? != RECORD_VERSION {
            return Err(Error::Malformed("record type"));
        }
        let body = T::read(&mut r)?;
        r.done()?;
        let s = Signed { body, sig: crate::keys::signature_bytes(sig)? };
        s.verify(key)?;
        Ok(s)
    }

    pub fn verify(&self, key: &PubKey) -> Result<()> {
        key.verify(T::DOMAIN, &Self::body_bytes(&self.body), &self.sig)
    }

    pub fn encode(&self) -> Vec<u8> {
        let mut v = Self::body_bytes(&self.body);
        v.extend_from_slice(&self.sig);
        v
    }

    pub fn read_from(r: &mut R) -> Result<Self> {
        if r.u8()? != T::TYPE {
            return Err(Error::Malformed("record type"));
        }
        if r.u8()? != RECORD_VERSION {
            return Err(Error::Malformed("record version"));
        }
        let body = T::read(r)?;
        let sig = r.arr::<SIG_LEN>()?;
        Ok(Signed { body, sig })
    }

    pub fn decode(b: &[u8]) -> Result<Self> {
        let mut r = R::new(b);
        let s = Self::read_from(&mut r)?;
        r.done()?;
        Ok(s)
    }
}

fn read_pub(r: &mut R) -> Result<PubKey> {
    PubKey::from_slice(&r.arr::<PUB_LEN>()?)
}

/// True if `[start, start+count)` contains `serial`.
pub fn range_contains(start: u64, count: u32, serial: u64) -> bool {
    serial >= start && serial - start < count as u64
}

/// True if the inner range sits entirely within the outer range.
pub fn range_within(outer_start: u64, outer_count: u32, inner_start: u64, inner_count: u32) -> bool {
    let (Some(outer_end), Some(inner_end)) = (
        outer_start.checked_add(outer_count as u64),
        inner_start.checked_add(inner_count as u64),
    ) else {
        return false;
    };
    inner_count > 0 && inner_start >= outer_start && inner_end <= outer_end
}

/// Vendor → studio: who the studio is, its key, and its limits.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct StudioCert {
    pub studio_id: u32,
    pub studio_key: PubKey,
    pub max_shops: u16,
    pub features: u32,
    pub vendor_key_id: [u8; 8],
}

impl Body for StudioCert {
    const TYPE: u8 = 0x01;
    const DOMAIN: &'static str = "mesob/studio-cert/v1";
    fn write(&self, w: &mut W) {
        w.u32(self.studio_id);
        w.bytes(&self.studio_key.0);
        w.u16(self.max_shops);
        w.u32(self.features);
        w.bytes(&self.vendor_key_id);
    }
    fn read(r: &mut R) -> Result<Self> {
        Ok(StudioCert {
            studio_id: r.u32()?,
            studio_key: read_pub(r)?,
            max_shops: r.u16()?,
            features: r.u32()?,
            vendor_key_id: r.arr()?,
        })
    }
}

/// Vendor → studio: a range of license serials the studio has paid for.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CapacityBlock {
    pub block_id: u32,
    pub studio_id: u32,
    pub serial_start: u64,
    pub serial_count: u32,
    pub vendor_key_id: [u8; 8],
}

impl Body for CapacityBlock {
    const TYPE: u8 = 0x02;
    const DOMAIN: &'static str = "mesob/capacity/v1";
    fn write(&self, w: &mut W) {
        w.u32(self.block_id);
        w.u32(self.studio_id);
        w.u64(self.serial_start);
        w.u32(self.serial_count);
        w.bytes(&self.vendor_key_id);
    }
    fn read(r: &mut R) -> Result<Self> {
        Ok(CapacityBlock {
            block_id: r.u32()?,
            studio_id: r.u32()?,
            serial_start: r.u64()?,
            serial_count: r.u32()?,
            vendor_key_id: r.arr()?,
        })
    }
}

/// Studio → shop: the shop's signing key and the key title keys are sealed to.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ShopCert {
    pub shop_id: u32,
    pub studio_id: u32,
    pub sign_key: PubKey,
    pub kex_key: PubKey,
}

impl Body for ShopCert {
    const TYPE: u8 = 0x03;
    const DOMAIN: &'static str = "mesob/shop-cert/v1";
    fn write(&self, w: &mut W) {
        w.u32(self.shop_id);
        w.u32(self.studio_id);
        w.bytes(&self.sign_key.0);
        w.bytes(&self.kex_key.0);
    }
    fn read(r: &mut R) -> Result<Self> {
        Ok(ShopCert {
            shop_id: r.u32()?,
            studio_id: r.u32()?,
            sign_key: read_pub(r)?,
            kex_key: read_pub(r)?,
        })
    }
}

/// Studio → shop: prepaid serials, carved out of one capacity block.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CreditPack {
    pub pack_id: u32,
    pub shop_id: u32,
    pub studio_id: u32,
    pub block_id: u32,
    pub serial_start: u64,
    pub serial_count: u32,
}

impl Body for CreditPack {
    const TYPE: u8 = 0x04;
    const DOMAIN: &'static str = "mesob/credit-pack/v1";
    fn write(&self, w: &mut W) {
        w.u32(self.pack_id);
        w.u32(self.shop_id);
        w.u32(self.studio_id);
        w.u32(self.block_id);
        w.u64(self.serial_start);
        w.u32(self.serial_count);
    }
    fn read(r: &mut R) -> Result<Self> {
        Ok(CreditPack {
            pack_id: r.u32()?,
            shop_id: r.u32()?,
            studio_id: r.u32()?,
            block_id: r.u32()?,
            serial_start: r.u64()?,
            serial_count: r.u32()?,
        })
    }
}

pub const LICENSE_FLAG_REISSUE: u8 = 0x01;

/// Shop → customer: one title, one device, one serial.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct License {
    pub serial: u64,
    pub title_id: [u8; 16],
    pub device_fp: [u8; 16],
    pub shop_id: u32,
    pub pack_id: u32,
    pub flags: u8,
    pub sealed_key: [u8; SEALED_DEVICE_LEN],
}

impl Body for License {
    const TYPE: u8 = 0x05;
    const DOMAIN: &'static str = "mesob/license/v1";
    fn write(&self, w: &mut W) {
        w.u64(self.serial);
        w.bytes(&self.title_id);
        w.bytes(&self.device_fp);
        w.u32(self.shop_id);
        w.u32(self.pack_id);
        w.u8(self.flags);
        w.bytes(&self.sealed_key);
    }
    fn read(r: &mut R) -> Result<Self> {
        Ok(License {
            serial: r.u64()?,
            title_id: r.arr()?,
            device_fp: r.arr()?,
            shop_id: r.u32()?,
            pack_id: r.u32()?,
            flags: r.u8()?,
            sealed_key: r.arr()?,
        })
    }
}
