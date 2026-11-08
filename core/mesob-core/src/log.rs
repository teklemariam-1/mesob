//! The shop's sales log: every entry is signed by the shop and carries the hash of the entry
//! before it. Removing, reordering or editing any entry breaks the chain, so the log can travel
//! on any USB stick or through any courier.

use crate::keys::{sha256, PubKey, SignKey};
use crate::records::{Body, Signed};
use crate::wire::{R, W};
use crate::{Error, Result};

pub const KIND_SALE: u8 = 1;
pub const KIND_REISSUE: u8 = 2;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SaleEntry {
    pub seq: u64,
    pub prev: [u8; 32],
    pub kind: u8,
    pub serial: u64,
    pub title_id: [u8; 16],
    pub device_fp: [u8; 16],
    /// Cash taken, in whole Nakfa.
    pub price: u32,
    /// The shop tablet's clock, for display only. Never used for any check.
    pub local_time: u64,
}

impl Body for SaleEntry {
    const TYPE: u8 = 0x10;
    const DOMAIN: &'static str = "mesob/sale/v1";
    fn write(&self, w: &mut W) {
        w.u64(self.seq);
        w.bytes(&self.prev);
        w.u8(self.kind);
        w.u64(self.serial);
        w.bytes(&self.title_id);
        w.bytes(&self.device_fp);
        w.u32(self.price);
        w.u64(self.local_time);
    }
    fn read(r: &mut R) -> Result<Self> {
        Ok(SaleEntry {
            seq: r.u64()?,
            prev: r.arr()?,
            kind: r.u8()?,
            serial: r.u64()?,
            title_id: r.arr()?,
            device_fp: r.arr()?,
            price: r.u32()?,
            local_time: r.u64()?,
        })
    }
}

pub fn entry_hash(e: &Signed<SaleEntry>) -> [u8; 32] {
    sha256(&[&e.encode()])
}

/// Shop: create the next entry after `prev` (or the first entry).
#[allow(clippy::too_many_arguments)]
pub fn append(
    prev: Option<&Signed<SaleEntry>>,
    shop_key: &SignKey,
    kind: u8,
    serial: u64,
    title_id: [u8; 16],
    device_fp: [u8; 16],
    price: u32,
    local_time: u64,
) -> Signed<SaleEntry> {
    Signed::sign(next_entry(prev, kind, serial, title_id, device_fp, price, local_time), shop_key)
}

/// The next unsigned entry, for shops whose signing key is held in hardware.
pub fn next_entry(
    prev: Option<&Signed<SaleEntry>>,
    kind: u8,
    serial: u64,
    title_id: [u8; 16],
    device_fp: [u8; 16],
    price: u32,
    local_time: u64,
) -> SaleEntry {
    let (seq, prev_hash) = match prev {
        Some(p) => (p.body.seq + 1, entry_hash(p)),
        None => (0, [0u8; 32]),
    };
    SaleEntry { seq, prev: prev_hash, kind, serial, title_id, device_fp, price, local_time }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct LogSummary {
    pub entries: u64,
    pub sales: u64,
    pub reissues: u64,
    pub cash: u64,
    pub next_seq: u64,
    pub last_hash: [u8; 32],
}

/// Studio: check a run of entries that continues from (`start_seq`, `start_prev`).
pub fn verify_chain(
    entries: &[Signed<SaleEntry>],
    shop_key: &PubKey,
    start_seq: u64,
    start_prev: [u8; 32],
) -> Result<LogSummary> {
    let mut s = LogSummary { next_seq: start_seq, last_hash: start_prev, ..Default::default() };
    for e in entries {
        if e.body.seq != s.next_seq {
            return Err(Error::Mismatch("sales log sequence (entry missing or reordered)"));
        }
        if e.body.prev != s.last_hash {
            return Err(Error::Mismatch("sales log chain (entry edited or removed)"));
        }
        e.verify(shop_key)?;
        match e.body.kind {
            KIND_SALE => {
                s.sales += 1;
                s.cash += e.body.price as u64;
            }
            KIND_REISSUE => s.reissues += 1,
            _ => return Err(Error::Malformed("sales log entry kind")),
        }
        s.entries += 1;
        s.next_seq += 1;
        s.last_hash = entry_hash(e);
    }
    Ok(s)
}
