//! C ABI for Windows (P/Invoke). Strings are UTF-8, NUL-terminated.
//!
//! ```c
//! char*   mesob_call(const char* request_json);   // free the result with mesob_free
//! void    mesob_free(char* s);
//! int64_t mesob_session_read(uint64_t handle, uint64_t offset, uint8_t* buf, size_t len);
//! int64_t mesob_session_size(uint64_t handle);
//! void    mesob_session_close(uint64_t handle);
//! ```

use std::ffi::{c_char, CStr, CString};

/// # Safety
/// `request` must be a valid NUL-terminated UTF-8 string.
#[no_mangle]
pub unsafe extern "C" fn mesob_call(request: *const c_char) -> *mut c_char {
    let response = if request.is_null() {
        r#"{"ok":false,"error":"null request"}"#.to_string()
    } else {
        match CStr::from_ptr(request).to_str() {
            Ok(s) => crate::dispatch(s),
            Err(_) => r#"{"ok":false,"error":"request is not UTF-8"}"#.to_string(),
        }
    };
    CString::new(response).unwrap_or_default().into_raw()
}

/// # Safety
/// `s` must come from `mesob_call` and be freed only once.
#[no_mangle]
pub unsafe extern "C" fn mesob_free(s: *mut c_char) {
    if !s.is_null() {
        drop(CString::from_raw(s));
    }
}

/// # Safety
/// `buf` must point to `len` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn mesob_session_read(handle: u64, offset: u64, buf: *mut u8, len: usize) -> i64 {
    if buf.is_null() {
        return -2;
    }
    crate::session_read(handle, offset, std::slice::from_raw_parts_mut(buf, len))
}

#[no_mangle]
pub extern "C" fn mesob_session_size(handle: u64) -> i64 {
    crate::session_size(handle)
}

#[no_mangle]
pub extern "C" fn mesob_session_close(handle: u64) {
    crate::session_close(handle)
}
