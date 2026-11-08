//! JNI for Android: `com.mesob.core.MesobNative`.
//!
//! ```kotlin
//! object MesobNative {
//!     external fun call(request: String): String
//!     external fun sessionRead(handle: Long, offset: Long, buf: ByteArray, off: Int, len: Int): Int
//!     external fun sessionSize(handle: Long): Long
//!     external fun sessionClose(handle: Long)
//! }
//! ```

use jni::objects::{JByteArray, JClass, JString};
use jni::sys::{jint, jlong, jstring};
use jni::JNIEnv;

#[no_mangle]
pub extern "system" fn Java_com_mesob_core_MesobNative_call<'l>(mut env: JNIEnv<'l>, _c: JClass<'l>, request: JString<'l>) -> jstring {
    let response = match env.get_string(&request) {
        Ok(s) => crate::dispatch(&String::from(s)),
        Err(_) => r#"{"ok":false,"error":"request is not a string"}"#.to_string(),
    };
    env.new_string(response).map(|s| s.into_raw()).unwrap_or(std::ptr::null_mut())
}

#[no_mangle]
pub extern "system" fn Java_com_mesob_core_MesobNative_sessionRead<'l>(
    env: JNIEnv<'l>,
    _c: JClass<'l>,
    handle: jlong,
    offset: jlong,
    buf: JByteArray<'l>,
    off: jint,
    len: jint,
) -> jint {
    if off < 0 || len < 0 || offset < 0 {
        return -2;
    }
    let mut tmp = vec![0u8; len as usize];
    let n = crate::session_read(handle as u64, offset as u64, &mut tmp);
    if n > 0 {
        // SAFETY: u8 and i8 have identical layout.
        let signed = unsafe { std::slice::from_raw_parts(tmp.as_ptr() as *const i8, n as usize) };
        if env.set_byte_array_region(&buf, off, signed).is_err() {
            return -2;
        }
    }
    tmp.iter_mut().for_each(|b| *b = 0);
    n as jint
}

#[no_mangle]
pub extern "system" fn Java_com_mesob_core_MesobNative_sessionSize<'l>(_env: JNIEnv<'l>, _c: JClass<'l>, handle: jlong) -> jlong {
    crate::session_size(handle as u64)
}

#[no_mangle]
pub extern "system" fn Java_com_mesob_core_MesobNative_sessionClose<'l>(_env: JNIEnv<'l>, _c: JClass<'l>, handle: jlong) {
    crate::session_close(handle as u64)
}
