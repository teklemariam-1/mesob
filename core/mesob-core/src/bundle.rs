//! The license bundle a player receives: the license plus every certificate needed to check it
//! offline back to the vendor root key built into the app. About 650 bytes, so it fits one QR code.
//!
//! ```text
//! "MSL1" | u16 len + studio cert | u16 len + capacity block | u16 len + shop cert
//!        | u16 len + credit pack | u16 len + license
//! ```

use crate::keys::{DeviceSecret, PubKey, SignKey};
use crate::records::{
    range_contains, range_within, CapacityBlock, CreditPack, License, ShopCert, Signed, StudioCert,
};
use crate::seal::{license_ctx, open_on_device, seal_to_device};
use crate::title::{Decryptor, TitleFile};
use crate::wire::R;
use crate::{Error, Result};
use std::io::{Read, Seek};

pub const MAGIC: &[u8; 4] = b"MSL1";

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LicenseBundle {
    pub studio: Signed<StudioCert>,
    pub capacity: Signed<CapacityBlock>,
    pub shop: Signed<ShopCert>,
    pub pack: Signed<CreditPack>,
    pub license: Signed<License>,
}

/// What a checked license grants.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Verified {
    pub serial: u64,
    pub title_id: [u8; 16],
    pub studio_id: u32,
    pub shop_id: u32,
    pub pack_id: u32,
    pub reissue: bool,
}

impl LicenseBundle {
    pub fn encode(&self) -> Vec<u8> {
        let mut v = MAGIC.to_vec();
        for part in [
            self.studio.encode(),
            self.capacity.encode(),
            self.shop.encode(),
            self.pack.encode(),
            self.license.encode(),
        ] {
            v.extend_from_slice(&(part.len() as u16).to_be_bytes());
            v.extend_from_slice(&part);
        }
        v
    }

    pub fn decode(b: &[u8]) -> Result<Self> {
        let mut r = R::new(b);
        if r.take(4)? != MAGIC {
            return Err(Error::Malformed("not a Mesob license"));
        }
        let mut part = || -> Result<&[u8]> {
            let n = r.u16()? as usize;
            r.take(n)
        };
        let bundle = LicenseBundle {
            studio: Signed::decode(part()?)?,
            capacity: Signed::decode(part()?)?,
            shop: Signed::decode(part()?)?,
            pack: Signed::decode(part()?)?,
            license: Signed::decode(part()?)?,
        };
        r.done()?;
        Ok(bundle)
    }

    /// Check the whole chain offline. Every link must hold, and the serial must sit inside the
    /// credit pack, which must sit inside the vendor's capacity block.
    pub fn verify(&self, vendor: &PubKey, device_fp: &[u8; 16]) -> Result<Verified> {
        let studio = &self.studio.body;
        if studio.vendor_key_id != vendor.key_id() {
            return Err(Error::Mismatch("vendor key"));
        }
        self.studio.verify(vendor)?;

        let cap = &self.capacity.body;
        if cap.vendor_key_id != vendor.key_id() || cap.studio_id != studio.studio_id {
            return Err(Error::Mismatch("capacity block"));
        }
        self.capacity.verify(vendor)?;

        let shop = &self.shop.body;
        if shop.studio_id != studio.studio_id {
            return Err(Error::Mismatch("shop studio"));
        }
        self.shop.verify(&studio.studio_key)?;
        if shop.shop_id == 0 || shop.shop_id > studio.max_shops as u32 {
            return Err(Error::OutOfRange("shop number beyond the studio's limit"));
        }

        let pack = &self.pack.body;
        if pack.studio_id != studio.studio_id || pack.shop_id != shop.shop_id || pack.block_id != cap.block_id {
            return Err(Error::Mismatch("credit pack"));
        }
        self.pack.verify(&studio.studio_key)?;
        if !range_within(cap.serial_start, cap.serial_count, pack.serial_start, pack.serial_count) {
            return Err(Error::OutOfRange("credit pack outside capacity block"));
        }

        let lic = &self.license.body;
        if lic.shop_id != shop.shop_id || lic.pack_id != pack.pack_id {
            return Err(Error::Mismatch("license shop or pack"));
        }
        self.license.verify(&shop.sign_key)?;
        if !range_contains(pack.serial_start, pack.serial_count, lic.serial) {
            return Err(Error::OutOfRange("license serial outside credit pack"));
        }
        if &lic.device_fp != device_fp {
            return Err(Error::Mismatch("license is for another device"));
        }
        Ok(Verified {
            serial: lic.serial,
            title_id: lic.title_id,
            studio_id: studio.studio_id,
            shop_id: shop.shop_id,
            pack_id: pack.pack_id,
            reissue: lic.flags & crate::records::LICENSE_FLAG_REISSUE != 0,
        })
    }

    /// Check the bundle for this device and this title, then return the decrypting reader.
    pub fn open_title<F: Read + Seek>(
        &self,
        title: TitleFile<F>,
        vendor: &PubKey,
        device: &DeviceSecret,
    ) -> Result<Decryptor<F>> {
        let v = self.verify(vendor, &device.fingerprint())?;
        if v.title_id != title.title_id() {
            return Err(Error::Mismatch("license is for another title"));
        }
        if title.studio.body.studio_id != v.studio_id || title.studio.body.studio_key != self.studio.body.studio_key {
            return Err(Error::Mismatch("title studio"));
        }
        let key = open_on_device(device, &self.license.body.sealed_key, &license_ctx(&v.title_id, v.serial))?;
        Ok(title.decryptor(key))
    }
}

/// Shop side: create one license. The caller (Shop app) is responsible for never reusing a serial
/// and for logging the sale; this only refuses serials outside the pack.
#[allow(clippy::too_many_arguments)]
pub fn issue_license(
    shop_key: &SignKey,
    shop_cert: &Signed<ShopCert>,
    pack: &Signed<CreditPack>,
    serial: u64,
    title_id: [u8; 16],
    title_key: &[u8; 32],
    device_pub: &[u8; 32],
    flags: u8,
) -> Result<Signed<License>> {
    if shop_key.public() != shop_cert.body.sign_key {
        return Err(Error::Mismatch("shop key and shop certificate"));
    }
    Ok(Signed::sign(license_body(shop_cert, pack, serial, title_id, title_key, device_pub, flags)?, shop_key))
}

/// The unsigned license, for shops whose signing key is held in hardware (see `Signed::prepare`).
pub fn license_body(
    shop_cert: &Signed<ShopCert>,
    pack: &Signed<CreditPack>,
    serial: u64,
    title_id: [u8; 16],
    title_key: &[u8; 32],
    device_pub: &[u8; 32],
    flags: u8,
) -> Result<License> {
    if pack.body.shop_id != shop_cert.body.shop_id {
        return Err(Error::Mismatch("credit pack belongs to another shop"));
    }
    if !range_contains(pack.body.serial_start, pack.body.serial_count, serial) {
        return Err(Error::OutOfRange("serial outside credit pack"));
    }
    let sealed_key = seal_to_device(device_pub, title_key, &license_ctx(&title_id, serial))?;
    Ok(License {
        serial,
        title_id,
        device_fp: crate::keys::device_fingerprint(device_pub),
        shop_id: shop_cert.body.shop_id,
        pack_id: pack.body.pack_id,
        flags,
        sealed_key,
    })
}
