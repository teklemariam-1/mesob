/// Every failure the core can report. Apps map these to user-facing messages.
#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("data ended early")]
    Truncated,
    #[error("malformed {0}")]
    Malformed(&'static str),
    #[error("signature check failed: {0}")]
    BadSignature(&'static str),
    #[error("does not match: {0}")]
    Mismatch(&'static str),
    #[error("out of range: {0}")]
    OutOfRange(&'static str),
    #[error("decryption failed")]
    Decrypt,
    #[error("invalid code: {0}")]
    Code(&'static str),
    #[error("damaged file: chunk {0} does not match its fingerprint")]
    Damaged(u32),
    #[error(transparent)]
    Io(#[from] std::io::Error),
}

pub type Result<T> = std::result::Result<T, Error>;
