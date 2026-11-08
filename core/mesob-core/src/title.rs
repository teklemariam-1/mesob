//! The `.msb` title file.
//!
//! ```text
//! "MSB1" | u32 len | studio certificate | u32 len | signed title header | body
//! ```
//!
//! The body is the original MP4 / M4A / PDF encrypted with AES-256-CTR under a random per-title
//! key. CTR lets any byte offset be decrypted directly, so players seek instantly and never write
//! plaintext to storage. The signed header lists a SHA-256 fingerprint of every ciphertext chunk,
//! so a damaged copy is detected before (and without) any license.

use crate::keys::{random, sha256, PubKey, SignKey};
use crate::records::{Body, Signed, StudioCert};
use crate::wire::{R, W};
use crate::{Error, Result};
use ctr::cipher::{KeyIvInit, StreamCipher, StreamCipherSeek};
use std::io::{self, Read, Seek, SeekFrom, Write};
use zeroize::Zeroize;

pub const MAGIC: &[u8; 4] = b"MSB1";
pub const DEFAULT_CHUNK: u32 = 1 << 20;
const MAX_STUDIO_CERT: u32 = 4 * 1024;
const MAX_HEADER: u32 = 4 * 1024 * 1024;
const MAX_TEXT: usize = 512;
const MAX_POSTER: usize = 512 * 1024;

type Aes256Ctr = ctr::Ctr128BE<aes::Aes256>;

fn cipher_at(key: &[u8; 32], iv: &[u8; 16], offset: u64) -> Aes256Ctr {
    let mut c = Aes256Ctr::new_from_slices(key, iv).expect("fixed key and IV sizes");
    c.seek(offset);
    c
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum Kind {
    Video = 1,
    Audio = 2,
    Pdf = 3,
}

impl Kind {
    pub fn from_u8(v: u8) -> Result<Self> {
        match v {
            1 => Ok(Kind::Video),
            2 => Ok(Kind::Audio),
            3 => Ok(Kind::Pdf),
            _ => Err(Error::Malformed("title kind")),
        }
    }
}

/// Studio-signed description of a title and its encrypted body.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TitleHeader {
    pub title_id: [u8; 16],
    pub studio_id: u32,
    pub kind: Kind,
    pub mime: String,
    pub title_en: String,
    pub title_ti: String,
    pub artist: String,
    pub plain_size: u64,
    pub chunk_size: u32,
    pub iv: [u8; 16],
    pub chunk_hashes: Vec<[u8; 32]>,
    pub poster: Vec<u8>,
}

fn chunk_count(plain_size: u64, chunk_size: u32) -> u64 {
    plain_size.div_ceil(chunk_size as u64)
}

impl Body for TitleHeader {
    const TYPE: u8 = 0x06;
    const DOMAIN: &'static str = "mesob/title/v1";
    fn write(&self, w: &mut W) {
        w.bytes(&self.title_id);
        w.u32(self.studio_id);
        w.u8(self.kind as u8);
        w.str(&self.mime);
        w.str(&self.title_en);
        w.str(&self.title_ti);
        w.str(&self.artist);
        w.u64(self.plain_size);
        w.u32(self.chunk_size);
        w.bytes(&self.iv);
        w.u32(self.chunk_hashes.len() as u32);
        for h in &self.chunk_hashes {
            w.bytes(h);
        }
        w.var(&self.poster);
    }
    fn read(r: &mut R) -> Result<Self> {
        let title_id = r.arr()?;
        let studio_id = r.u32()?;
        let kind = Kind::from_u8(r.u8()?)?;
        let mime = r.string(64)?;
        let title_en = r.string(MAX_TEXT)?;
        let title_ti = r.string(MAX_TEXT)?;
        let artist = r.string(MAX_TEXT)?;
        let plain_size = r.u64()?;
        let chunk_size = r.u32()?;
        if !(4096..=64 * 1024 * 1024).contains(&chunk_size) {
            return Err(Error::Malformed("chunk size"));
        }
        let iv = r.arr()?;
        let n = r.u32()? as u64;
        if n != chunk_count(plain_size, chunk_size) {
            return Err(Error::Malformed("chunk list"));
        }
        let mut chunk_hashes = Vec::with_capacity(n as usize);
        for _ in 0..n {
            chunk_hashes.push(r.arr()?);
        }
        let poster = r.var(MAX_POSTER)?.to_vec();
        Ok(TitleHeader { title_id, studio_id, kind, mime, title_en, title_ti, artist, plain_size, chunk_size, iv, chunk_hashes, poster })
    }
}

/// What the studio enters for a title.
#[derive(Clone, Debug)]
pub struct TitleMeta {
    pub kind: Kind,
    pub mime: String,
    pub title_en: String,
    pub title_ti: String,
    pub artist: String,
    pub poster: Vec<u8>,
}

/// Result of packaging: the studio must keep `title_key` (it is never stored in the file).
pub struct Packaged {
    pub title_id: [u8; 16],
    pub title_key: [u8; 32],
    pub plain_size: u64,
}

fn copy_encrypted<I: Read, O: Write>(
    input: &mut I,
    mut sink: O,
    key: &[u8; 32],
    iv: &[u8; 16],
    plain_size: u64,
    chunk_size: u32,
    mut on_chunk: impl FnMut(&[u8]),
) -> Result<()> {
    let mut c = cipher_at(key, iv, 0);
    let mut buf = vec![0u8; chunk_size as usize];
    let mut done = 0u64;
    while done < plain_size {
        let n = (plain_size - done).min(chunk_size as u64) as usize;
        input.read_exact(&mut buf[..n])?;
        c.apply_keystream(&mut buf[..n]);
        on_chunk(&buf[..n]);
        sink.write_all(&buf[..n])?;
        done += n as u64;
    }
    buf.zeroize();
    Ok(())
}

/// Encrypt `input` into a `.msb` file. Reads the input twice (fingerprints first, then body).
pub fn package<I: Read + Seek, O: Write>(
    input: &mut I,
    out: &mut O,
    studio_cert: &Signed<StudioCert>,
    studio_key: &SignKey,
    meta: TitleMeta,
    chunk_size: u32,
) -> Result<Packaged> {
    if studio_key.public() != studio_cert.body.studio_key {
        return Err(Error::Mismatch("studio key and studio certificate"));
    }
    let plain_size = input.seek(SeekFrom::End(0))?;
    input.seek(SeekFrom::Start(0))?;
    let title_id = random::<16>();
    let title_key = random::<32>();
    let iv = random::<16>();

    let mut chunk_hashes = Vec::new();
    copy_encrypted(input, io::sink(), &title_key, &iv, plain_size, chunk_size, |c| chunk_hashes.push(sha256(&[c])))?;

    let header = Signed::sign(
        TitleHeader {
            title_id,
            studio_id: studio_cert.body.studio_id,
            kind: meta.kind,
            mime: meta.mime,
            title_en: meta.title_en,
            title_ti: meta.title_ti,
            artist: meta.artist,
            plain_size,
            chunk_size,
            iv,
            chunk_hashes,
            poster: meta.poster,
        },
        studio_key,
    );
    let sc = studio_cert.encode();
    let hb = header.encode();
    out.write_all(MAGIC)?;
    out.write_all(&(sc.len() as u32).to_be_bytes())?;
    out.write_all(&sc)?;
    out.write_all(&(hb.len() as u32).to_be_bytes())?;
    out.write_all(&hb)?;

    input.seek(SeekFrom::Start(0))?;
    copy_encrypted(input, &mut *out, &title_key, &iv, plain_size, chunk_size, |_| {})?;
    out.flush()?;
    Ok(Packaged { title_id, title_key, plain_size })
}

fn read_u32<F: Read>(f: &mut F) -> Result<u32> {
    let mut b = [0u8; 4];
    f.read_exact(&mut b)?;
    Ok(u32::from_be_bytes(b))
}

fn read_vec<F: Read>(f: &mut F, n: u32) -> Result<Vec<u8>> {
    let mut v = vec![0u8; n as usize];
    f.read_exact(&mut v)?;
    Ok(v)
}

/// An opened, signature-checked title file. Opening needs no license.
pub struct TitleFile<F> {
    file: F,
    pub studio: Signed<StudioCert>,
    pub header: Signed<TitleHeader>,
    body_offset: u64,
}

impl<F: Read + Seek> TitleFile<F> {
    /// Parse and check the file against the vendor root key.
    pub fn open(mut file: F, vendor: &PubKey) -> Result<Self> {
        file.seek(SeekFrom::Start(0))?;
        let mut m = [0u8; 4];
        file.read_exact(&mut m).map_err(|_| Error::Malformed("not a Mesob title file"))?;
        if &m != MAGIC {
            return Err(Error::Malformed("not a Mesob title file"));
        }
        let sc_len = read_u32(&mut file)?;
        if sc_len > MAX_STUDIO_CERT {
            return Err(Error::Malformed("studio certificate length"));
        }
        let studio = Signed::<StudioCert>::decode(&read_vec(&mut file, sc_len)?)?;
        if studio.body.vendor_key_id != vendor.key_id() {
            return Err(Error::Mismatch("vendor key"));
        }
        studio.verify(vendor)?;
        let h_len = read_u32(&mut file)?;
        if h_len > MAX_HEADER {
            return Err(Error::Malformed("title header length"));
        }
        let header = Signed::<TitleHeader>::decode(&read_vec(&mut file, h_len)?)?;
        header.verify(&studio.body.studio_key)?;
        if header.body.studio_id != studio.body.studio_id {
            return Err(Error::Mismatch("title studio"));
        }
        let body_offset = 4 + 4 + sc_len as u64 + 4 + h_len as u64;
        let end = file.seek(SeekFrom::End(0))?;
        if end != body_offset + header.body.plain_size {
            return Err(Error::Malformed("file is incomplete or has extra data"));
        }
        Ok(TitleFile { file, studio, header, body_offset })
    }

    pub fn title_id(&self) -> [u8; 16] {
        self.header.body.title_id
    }

    /// Check every chunk against its signed fingerprint (e.g. after copying from USB).
    pub fn check_integrity(&mut self) -> Result<()> {
        let h = &self.header.body;
        self.file.seek(SeekFrom::Start(self.body_offset))?;
        let mut buf = vec![0u8; h.chunk_size as usize];
        let mut left = h.plain_size;
        for (i, expected) in h.chunk_hashes.iter().enumerate() {
            let n = left.min(h.chunk_size as u64) as usize;
            self.file.read_exact(&mut buf[..n])?;
            if &sha256(&[&buf[..n]]) != expected {
                return Err(Error::Damaged(i as u32));
            }
            left -= n as u64;
        }
        Ok(())
    }

    /// Plaintext view of the body. The caller has already checked the license for this title.
    pub fn decryptor(self, title_key: [u8; 32]) -> Decryptor<F> {
        Decryptor {
            file: self.file,
            key: title_key,
            iv: self.header.body.iv,
            body_offset: self.body_offset,
            size: self.header.body.plain_size,
            pos: 0,
        }
    }
}

/// `Read + Seek` over the decrypted title. Plaintext only ever exists in the caller's buffer.
pub struct Decryptor<F> {
    file: F,
    key: [u8; 32],
    iv: [u8; 16],
    body_offset: u64,
    size: u64,
    pos: u64,
}

impl<F> Decryptor<F> {
    pub fn len(&self) -> u64 {
        self.size
    }
    pub fn is_empty(&self) -> bool {
        self.size == 0
    }
}

impl<F: Read + Seek> Read for Decryptor<F> {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if self.pos >= self.size || buf.is_empty() {
            return Ok(0);
        }
        let n = (buf.len() as u64).min(self.size - self.pos) as usize;
        self.file.seek(SeekFrom::Start(self.body_offset + self.pos))?;
        let got = self.file.read(&mut buf[..n])?;
        if got == 0 {
            return Err(io::ErrorKind::UnexpectedEof.into());
        }
        cipher_at(&self.key, &self.iv, self.pos).apply_keystream(&mut buf[..got]);
        self.pos += got as u64;
        Ok(got)
    }
}

impl<F: Read + Seek> Seek for Decryptor<F> {
    fn seek(&mut self, to: SeekFrom) -> io::Result<u64> {
        let target = match to {
            SeekFrom::Start(o) => Some(o),
            SeekFrom::End(d) => self.size.checked_add_signed(d),
            SeekFrom::Current(d) => self.pos.checked_add_signed(d),
        };
        let target = target.ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "seek before start"))?;
        self.pos = target;
        Ok(target)
    }
}

impl<F> Drop for Decryptor<F> {
    fn drop(&mut self) {
        self.key.zeroize();
    }
}
