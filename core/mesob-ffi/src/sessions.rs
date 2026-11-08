//! Open playback sessions: a decrypting reader per handle. Plaintext only ever lands in the
//! caller's buffer; nothing is written to storage.

use mesob_core::title::Decryptor;
use std::collections::HashMap;
use std::fs::File;
use std::io::{BufReader, Read, Seek, SeekFrom};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};

type Reader = Decryptor<BufReader<File>>;

fn table() -> &'static Mutex<HashMap<u64, Reader>> {
    static T: OnceLock<Mutex<HashMap<u64, Reader>>> = OnceLock::new();
    T.get_or_init(|| Mutex::new(HashMap::new()))
}

static NEXT: AtomicU64 = AtomicU64::new(1);

pub(crate) fn insert(r: Reader) -> u64 {
    let h = NEXT.fetch_add(1, Ordering::Relaxed);
    table().lock().unwrap_or_else(|e| e.into_inner()).insert(h, r);
    h
}

/// Read up to `buf.len()` decrypted bytes at `offset`. Returns bytes read (0 at end), or -1 if
/// the handle is unknown, -2 on an I/O error.
pub fn session_read(handle: u64, offset: u64, buf: &mut [u8]) -> i64 {
    let mut t = table().lock().unwrap_or_else(|e| e.into_inner());
    let Some(r) = t.get_mut(&handle) else { return -1 };
    if r.seek(SeekFrom::Start(offset)).is_err() {
        return -2;
    }
    let mut filled = 0;
    while filled < buf.len() {
        match r.read(&mut buf[filled..]) {
            Ok(0) => break,
            Ok(n) => filled += n,
            Err(_) => return -2,
        }
    }
    filled as i64
}

/// Plaintext size of the session's title, or -1 if the handle is unknown.
pub fn session_size(handle: u64) -> i64 {
    let t = table().lock().unwrap_or_else(|e| e.into_inner());
    t.get(&handle).map(|r| r.len() as i64).unwrap_or(-1)
}

/// Close a session and wipe its key.
pub fn session_close(handle: u64) {
    table().lock().unwrap_or_else(|e| e.into_inner()).remove(&handle);
}
