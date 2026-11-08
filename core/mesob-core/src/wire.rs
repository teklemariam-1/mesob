//! Minimal big-endian binary encoding. Every record has exactly one encoding, so a signature
//! over the re-encoded body is a signature over what was parsed.

use crate::{Error, Result};

#[derive(Default)]
pub struct W(pub Vec<u8>);

impl W {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn u8(&mut self, v: u8) {
        self.0.push(v)
    }
    pub fn u16(&mut self, v: u16) {
        self.0.extend_from_slice(&v.to_be_bytes())
    }
    pub fn u32(&mut self, v: u32) {
        self.0.extend_from_slice(&v.to_be_bytes())
    }
    pub fn u64(&mut self, v: u64) {
        self.0.extend_from_slice(&v.to_be_bytes())
    }
    pub fn bytes(&mut self, b: &[u8]) {
        self.0.extend_from_slice(b)
    }
    /// Length-prefixed (u32) bytes.
    pub fn var(&mut self, b: &[u8]) {
        self.u32(b.len() as u32);
        self.bytes(b)
    }
    pub fn str(&mut self, s: &str) {
        self.var(s.as_bytes())
    }
}

pub struct R<'a> {
    b: &'a [u8],
    p: usize,
}

impl<'a> R<'a> {
    pub fn new(b: &'a [u8]) -> Self {
        R { b, p: 0 }
    }
    pub fn take(&mut self, n: usize) -> Result<&'a [u8]> {
        if self.b.len() - self.p < n {
            return Err(Error::Truncated);
        }
        let s = &self.b[self.p..self.p + n];
        self.p += n;
        Ok(s)
    }
    pub fn arr<const N: usize>(&mut self) -> Result<[u8; N]> {
        let mut a = [0u8; N];
        a.copy_from_slice(self.take(N)?);
        Ok(a)
    }
    pub fn u8(&mut self) -> Result<u8> {
        Ok(self.take(1)?[0])
    }
    pub fn u16(&mut self) -> Result<u16> {
        Ok(u16::from_be_bytes(self.arr()?))
    }
    pub fn u32(&mut self) -> Result<u32> {
        Ok(u32::from_be_bytes(self.arr()?))
    }
    pub fn u64(&mut self) -> Result<u64> {
        Ok(u64::from_be_bytes(self.arr()?))
    }
    /// Length-prefixed bytes, refusing anything longer than `max`.
    pub fn var(&mut self, max: usize) -> Result<&'a [u8]> {
        let n = self.u32()? as usize;
        if n > max {
            return Err(Error::Malformed("length"));
        }
        self.take(n)
    }
    pub fn string(&mut self, max: usize) -> Result<String> {
        String::from_utf8(self.var(max)?.to_vec()).map_err(|_| Error::Malformed("text"))
    }
    pub fn done(&self) -> Result<()> {
        if self.p == self.b.len() {
            Ok(())
        } else {
            Err(Error::Malformed("trailing bytes"))
        }
    }
}
