# Mesob formats

All integers are big-endian. Public keys are P-256 SEC1 compressed (33 bytes); signatures are
ECDSA P-256 / SHA-256 as raw `r || s` (64 bytes). Implementation: `core/mesob-core`.

## Signed records

```
type (u8) | version (u8 = 1) | fields… | signature (64)
signature covers:  domain || 0x00 || type | version | fields
```

| Type | Record | Signed by | Domain | Fields |
|------|--------|-----------|--------|--------|
| 0x01 | Studio certificate | vendor | `mesob/studio-cert/v1` | studio_id u32 · studio_key 33 · max_shops u16 · features u32 · vendor_key_id 8 |
| 0x02 | Capacity block | vendor | `mesob/capacity/v1` | block_id u32 · studio_id u32 · serial_start u64 · serial_count u32 · vendor_key_id 8 |
| 0x03 | Shop certificate | studio | `mesob/shop-cert/v1` | shop_id u32 · studio_id u32 · sign_key 33 · kex_key 33 |
| 0x04 | Credit pack | studio | `mesob/credit-pack/v1` | pack_id u32 · shop_id u32 · studio_id u32 · block_id u32 · serial_start u64 · serial_count u32 |
| 0x05 | License | shop | `mesob/license/v1` | serial u64 · title_id 16 · device_fp 16 · shop_id u32 · pack_id u32 · flags u8 · sealed_key 80 |
| 0x06 | Title header | studio | `mesob/title/v1` | see below |
| 0x10 | Sale log entry | shop | `mesob/sale/v1` | seq u64 · prev_hash 32 · kind u8 · serial u64 · title_id 16 · device_fp 16 · price u32 · local_time u64 |

`key_id` = first 8 bytes of SHA-256("mesob/key-id/v1" || public key).
`device_fp` = first 16 bytes of SHA-256("mesob/device/v1" || X25519 device public key).
License flag `0x01` = replacement (re-issue). Log kind 1 = sale, 2 = replacement.
The catalog JSON is signed by the studio with domain `mesob/catalog/v1` (detached `catalog.sig`).

## License bundle (`.msl`, QR code)

```
"MSL1" | u16 len + studio cert | u16 len + capacity block | u16 len + shop cert
       | u16 len + credit pack | u16 len + license                     (≈ 650 bytes)
```

A player accepts it only if: studio cert and capacity are signed by the vendor key compiled into
the core; shop cert and pack are signed by that studio; shop_id ≤ max_shops; pack ⊂ capacity;
license signed by the shop; serial ∈ pack; device_fp is this device. A `.msls` file is a list of
bundles, each prefixed by a u16 length.

## Title file (`.msb`)

```
"MSB1" | u32 len | studio certificate | u32 len | signed title header | body
```

Header fields: title_id 16 · studio_id u32 · kind u8 (1 video, 2 audio, 3 pdf) · mime · title_en ·
title_ti · artist (u32-length UTF-8 each) · plain_size u64 · chunk_size u32 · iv 16 ·
chunk count u32 + SHA-256 of each ciphertext chunk · poster (u32-length bytes, ≤ 512 KiB).

Body: the original MP4 / M4A / PDF, AES-256-CTR (128-bit big-endian counter from `iv`) under a
random per-title key. The title key is never in the file.

## Sealing a title key

* To a device: one-time X25519 → HKDF-SHA256 (salt = eph_pub || recipient_pub, info =
  "mesob/seal/v1\0" || context) → AES-256-GCM. 32 + 48 = 80 bytes.
  Context = "license" || title_id || serial.
* To a shop: the same with P-256 ECDH, 33 + 48 = 81 bytes (Android Keystore can do the ECDH).
  Context = "shop-title" || title_id, or "shop-code-secret" || shop_id for the code secret.

## Device request (`.msr`, QR code)

```
"MSR1" | device public key 32 | n u8 | n × title_id 16
```

## Short codes

Crockford base32, groups of 4, case-insensitive, I/L→1, O→0.

* Credit unlock (20 chars): kind 1 · pack_id u32 · 56-bit HMAC
* Daily report (31 chars): kind 2 · day u16 · count u16 · cash u32 · log_tag 3 · 56-bit HMAC

HMAC-SHA256 over "mesob/code/v1\0" || shop_id || payload, keyed with the per-shop code secret.
