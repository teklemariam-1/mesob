//! Bridge from the apps to `mesob-core`.
//!
//! One JSON request/response entry point covers every operation (`call`), plus three raw calls
//! for playback, where speed matters: `session_read`, `session_size`, `session_close`.
//!
//! * Windows (C#, P/Invoke): `mesob_call`, `mesob_free`, `mesob_session_*` (see `capi`)
//! * Android (Kotlin, JNI):  `com.mesob.core.MesobNative` (see `jni_api`)
//!
//! The vendor root key is compiled in from the `MESOB_VENDOR_PUB` environment variable (hex) at
//! build time. Only lab builds without it accept a `vendor_pub` field in requests, and they
//! report `"lab": true` from `core.info`, so a lab build can never be shipped by mistake unnoticed.

mod ops;
mod sessions;

pub mod capi;
#[cfg(target_os = "android")]
mod jni_api;

pub use ops::dispatch;
pub use sessions::{session_close, session_read, session_size};
