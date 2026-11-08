//! Short codes typed by hand, sent by SMS or read over the phone.
//!
//! * Credit unlock (studio → shop): 20 characters, e.g. `7KQ2-M9XD-4TPA-W8RN-C3GH`
//! * Daily report (shop → studio): 31 characters
//!
//! Each code carries a 56-bit HMAC-SHA256 tag made with a secret shared by that shop and the
//! studio (set when the shop is set up). Codes use Crockford base32: no I, L, O or U; typing
//! I/L as 1 and O as 0 is accepted, case does not matter, dashes and spaces are ignored.
//! One-time use (each pack unlocks once, each report day is counted once) is enforced by the apps.

use crate::{Error, Result};
use hmac::{Hmac, Mac};
use sha2::Sha256;

type HmacSha256 = Hmac<Sha256>;

const ALPHABET: &[u8; 32] = b"0123456789ABCDEFGHJKMNPQRSTVWXYZ";
const TAG_LEN: usize = 7;
pub const KIND_UNLOCK: u8 = 1;
pub const KIND_REPORT: u8 = 2;
const UNLOCK_LEN: usize = 1 + 4 + TAG_LEN;
const REPORT_LEN: usize = 1 + 2 + 2 + 4 + 3 + TAG_LEN;

fn mac(secret: &[u8; 32], shop_id: u32, data: &[u8]) -> HmacSha256 {
    let mut m = HmacSha256::new_from_slice(secret).expect("HMAC takes any key length");
    m.update(b"mesob/code/v1\0");
    m.update(&shop_id.to_be_bytes());
    m.update(data);
    m
}

fn encode(bytes: &[u8]) -> String {
    let (mut acc, mut bits) = (0u32, 0u32);
    let mut chars = Vec::new();
    for &b in bytes {
        acc = (acc << 8) | b as u32;
        bits += 8;
        while bits >= 5 {
            bits -= 5;
            chars.push(ALPHABET[((acc >> bits) & 31) as usize]);
        }
    }
    if bits > 0 {
        chars.push(ALPHABET[((acc << (5 - bits)) & 31) as usize]);
    }
    chars
        .chunks(4)
        .map(|g| String::from_utf8(g.to_vec()).expect("ASCII"))
        .collect::<Vec<_>>()
        .join("-")
}

fn value(c: char) -> Option<u32> {
    let c = match c.to_ascii_uppercase() {
        'I' | 'L' => '1',
        'O' => '0',
        c => c,
    };
    ALPHABET.iter().position(|&a| a as char == c).map(|p| p as u32)
}

fn decode(code: &str, nbytes: usize) -> Result<Vec<u8>> {
    let vals: Vec<u32> = code
        .chars()
        .filter(|c| *c != '-' && !c.is_whitespace())
        .map(|c| value(c).ok_or(Error::Code("unknown character")))
        .collect::<Result<_>>()?;
    if vals.len() != (nbytes * 8).div_ceil(5) {
        return Err(Error::Code("wrong length"));
    }
    let (mut acc, mut bits) = (0u32, 0u32);
    let mut out = Vec::with_capacity(nbytes);
    for v in vals {
        acc = (acc << 5) | v;
        bits += 5;
        if bits >= 8 {
            bits -= 8;
            if out.len() < nbytes {
                out.push((acc >> bits) as u8);
            }
        }
    }
    if acc & ((1 << bits) - 1) != 0 {
        return Err(Error::Code("wrong padding"));
    }
    Ok(out)
}

fn check(secret: &[u8; 32], shop_id: u32, bytes: &[u8]) -> Result<()> {
    let (data, tag) = bytes.split_at(bytes.len() - TAG_LEN);
    mac(secret, shop_id, data)
        .verify_truncated_left(tag)
        .map_err(|_| Error::Code("does not match this shop"))
}

/// Studio: code that unlocks pre-delivered credit pack `pack_id` on shop `shop_id`.
pub fn unlock_code(secret: &[u8; 32], shop_id: u32, pack_id: u32) -> String {
    let mut data = vec![KIND_UNLOCK];
    data.extend_from_slice(&pack_id.to_be_bytes());
    let tag = mac(secret, shop_id, &data).finalize().into_bytes();
    data.extend_from_slice(&tag[..TAG_LEN]);
    encode(&data)
}

/// Shop: check a typed unlock code and return the pack it unlocks.
pub fn read_unlock(secret: &[u8; 32], shop_id: u32, code: &str) -> Result<u32> {
    let b = decode(code, UNLOCK_LEN)?;
    if b[0] != KIND_UNLOCK {
        return Err(Error::Code("not a credit code"));
    }
    check(secret, shop_id, &b)?;
    Ok(u32::from_be_bytes(b[1..5].try_into().expect("4 bytes")))
}

/// A shop's daily totals. `log_tag` is the first 3 bytes of the sales log's latest entry hash,
/// so the full log arriving later on a sync stick must match the codes already received.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Report {
    pub day: u16,
    pub count: u16,
    pub cash: u32,
    pub log_tag: [u8; 3],
}

pub fn report_code(secret: &[u8; 32], shop_id: u32, r: &Report) -> String {
    let mut data = vec![KIND_REPORT];
    data.extend_from_slice(&r.day.to_be_bytes());
    data.extend_from_slice(&r.count.to_be_bytes());
    data.extend_from_slice(&r.cash.to_be_bytes());
    data.extend_from_slice(&r.log_tag);
    let tag = mac(secret, shop_id, &data).finalize().into_bytes();
    data.extend_from_slice(&tag[..TAG_LEN]);
    encode(&data)
}

pub fn read_report(secret: &[u8; 32], shop_id: u32, code: &str) -> Result<Report> {
    let b = decode(code, REPORT_LEN)?;
    if b[0] != KIND_REPORT {
        return Err(Error::Code("not a report code"));
    }
    check(secret, shop_id, &b)?;
    Ok(Report {
        day: u16::from_be_bytes([b[1], b[2]]),
        count: u16::from_be_bytes([b[3], b[4]]),
        cash: u32::from_be_bytes(b[5..9].try_into().expect("4 bytes")),
        log_tag: [b[9], b[10], b[11]],
    })
}
