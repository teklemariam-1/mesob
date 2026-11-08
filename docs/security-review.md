# Mesob copy-protection review

Status: v0.1, reviewed 7 November 2026 against the code in this repository.

## Summary

With no internet anywhere, Mesob stops the ways content actually spreads in Eritrea: copying files
by USB, Xender or Bluetooth, sharing a license, a shop selling without paying, and a studio
selling beyond what it bought from the vendor. Each of these is checked by tests that run against
the real code (`cargo test`, `Mesob.SystemTest`).

What no offline system can stop: someone who breaks open a rooted phone or a debugged PC can, with
skill, pull out a title key and share the plain file; and a camera pointed at a screen. For those,
Mesob makes the work slow and specialised, and the on-screen watermark traces filmed copies to the
sale. See "What remains" below.

## How each attack is stopped

| # | Attack | What stops it | Kind | Proven by |
|---|--------|---------------|------|-----------|
| 1 | Copy a title file to a friend (USB, Xender, Bluetooth) | File is AES-256 encrypted with its own key; the key only exists sealed to the buyer's device | Cryptographic | `file_alone_does_not_contain_plaintext`, SystemTest "no plaintext" |
| 2 | Copy the file **and** the license to a friend | License names the buyer's device; the title key is sealed (X25519) to that device's key | Cryptographic | `shared_file_does_not_play_on_another_device`, SystemTest "useless on a friend's device" |
| 3 | Edit a license (another title, serial, device) | Shop signature (ECDSA P-256) over the whole license | Cryptographic | `edited_license_is_rejected`, `license_for_one_title_does_not_open_another` |
| 4 | Shop sells beyond its prepaid credit (serial outside its pack) | Player checks serial ∈ credit pack ∈ vendor capacity block | Cryptographic | `shop_cannot_sell_outside_its_pack`, SystemTest "serial outside the pack" |
| 5 | Studio sells beyond the capacity it bought | Credit packs must sit inside a vendor-signed capacity block | Cryptographic | `studio_cannot_exceed_vendor_capacity` |
| 6 | Someone runs a pirate network with copies of our apps | Every chain must end at the vendor root key compiled into the core | Cryptographic | `pirate_vendor_key_is_rejected` |
| 7 | A lab (development) core is shipped by mistake | Release builds don't compile without the vendor key or an explicit `--features lab` | Build | `cargo build --release -p mesob-ffi` fails without either |
| 8 | Studio exceeds its shop limit | Shop number must be ≤ the limit in the vendor's studio certificate | Cryptographic | `shop_beyond_studio_limit_is_rejected` |
| 9 | Fake shop on the Wi-Fi | Files and licenses are signed back to the vendor; a fake shop can't make any the Player accepts | Cryptographic | (follows from 3–6) |
| 10 | Damaged or edited title file | Signed header with a SHA-256 fingerprint for every 1 MiB chunk | Cryptographic | `damaged_or_edited_title_file_is_detected` |
| 11 | Shop hides sales (edits, deletes or reorders its log) | Every entry is signed and chained to the previous one; the studio rejects gaps, edits and a log that got shorter | Cryptographic | `sales_log_detects_edits_and_gaps`, SystemTest "edited/shortened log rejected" |
| 12 | Same serial sold twice (cloned or abused shop) | Studio Control alerts on duplicate serials, serials outside packs, and sales from packs never unlocked | Detection | SystemTest "serial sold twice raises an alert" |
| 13 | Daily report code doesn't match the log that arrives later | Report code carries the log's latest fingerprint; Studio Control alerts on mismatch | Detection | SystemTest report code |
| 14 | Forged credit or report code, or a code for another shop | 56-bit HMAC with a per-shop secret delivered sealed to the shop's key | Cryptographic | `codes_round_trip_and_reject_tampering` |
| 15 | Shop keys copied off a tablet | Signing key generated inside Android Keystore (non-exportable, app-bound); key-exchange key in Keystore on Android 12+ | Hardware | SystemTest runs the same prepare → hardware-sign → finish path; on-device test pending |
| 16 | Screenshots and screen recording | Android `FLAG_SECURE` (whole app); Windows `WDA_EXCLUDEFROMCAPTURE` | Platform | On-device test pending |
| 17 | Debugger attached (Windows), debuggable/re-signed/rooted/emulator (Android) | Playback refuses to start | Software | On-device test pending |
| 18 | Device key or licenses leave via backup | `allowBackup=false` and data-extraction rules exclude everything; keys locked by Keystore/TPM | Platform | Manifest |

## What remains (honest limits)

1. **Rooted phones and debugged PCs.** The title key and the decrypted stream exist in app memory
   while playing; there is no offline equivalent of Netflix's hardware DRM (Widevine L1) available
   to us. A skilled attacker on a rooted phone can extract a title key and share the plain file.
   *Mitigations in place:* keys handled only in native code, a separate key per title, root,
   emulator, debugger and signature checks, visible watermark. *Planned:* per-sale forensic
   watermark (phase 2), commercial obfuscation (DexGuard), outside security review.
2. **Camera filming a screen.** Can't be blocked anywhere. The drifting watermark (license serial +
   device code) traces the copy to the sale and the shop.
3. **Serial reuse is not provable offline.** A phone can't know a serial was already used on
   another phone. The limit on how many licenses a shop or studio issues therefore rests on their
   keys living in hardware, inside our software, on devices they can't take over (finding 1 of the
   architecture review). Shop tablets: Keystore key on a locked bootloader. Studio: a locked-down
   PC we set up. Duplicates that do reach a sales log are flagged (row 12).
4. **Older devices are weaker.** Android 7–10 PDF reading uses a private temp file deleted on
   open (Android 11+ uses memory only). Shop tablets below Android 12 keep the key-exchange key in
   software locked by Keystore. Windows PCs without TPM 2.0 lock keys with DPAPI only.
5. **Not yet verified at the studio:** the shop signing key's attestation chain is recorded with
   each shop but not yet checked against Google's attestation root.

## Must be done before the pilot

- [ ] Build production cores with `MESOB_VENDOR_PUB` set (never ship a lab core; `core.info` reports `"lab": true`).
- [ ] Generate the real vendor root key on a hardware security key; retire the `lab/` key.
- [ ] Sign release APKs with an offline release key and build with `-Pmesob.signingCertSha256=…`.
- [ ] Code-signing certificate for the Windows installers.
- [ ] Studio PC lockdown: BitLocker + TPM, standard accounts, App Control allow-list.
- [ ] Shop tablets: Android 12+, locked bootloader, from the tested model list.
- [ ] Verify shop key attestation chains in Studio Control.
- [ ] On-device tests: the acceptance scenarios in the plan, on the device lab (cheap Android phones, Android TV box, shop tablets, Windows 10/11 with and without TPM).
- [ ] Outside security review of the core and the Shop app.
