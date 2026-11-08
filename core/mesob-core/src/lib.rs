//! Mesob shared security core.
//!
//! Everything that touches keys lives here and is shared by every app (Player for Android and
//! Windows, Shop app, Studio Control, Vendor Console):
//!
//! * [`records`]: signed certificates, capacity blocks, credit packs and licenses
//! * [`bundle`]: the license bundle a player receives, and checking it back to the vendor root
//! * [`title`]: the encrypted `.msb` title file, packaging and decrypt-on-read
//! * [`seal`]: locking a title key to one device (X25519) or one shop (P-256)
//! * [`codes`]: short typed codes for credit unlocks and daily reports
//! * [`log`]: the shop's signed, chained sales log
//!
//! Formats are specified in `docs/spec-formats.md`. Nothing in this crate reads a clock: no
//! security decision depends on the date.

pub mod bundle;
pub mod codes;
pub mod error;
pub mod keys;
pub mod log;
pub mod records;
pub mod seal;
pub mod title;
pub mod wire;

pub use error::{Error, Result};
