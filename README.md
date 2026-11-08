# Mesob

Offline, cash-only distribution of protected video, music and PDF books in Eritrea. Customers buy
at distributor shops; what they buy plays only on their own device; the studio sees every sale;
nothing needs the internet.

Plan and architecture review: https://claude.ai/artifact/4DNAqtqVxKK9hT4ZrbM6TX ·
Formats: [docs/spec-formats.md](docs/spec-formats.md) · Sticks and shop network:
[docs/spec-sync.md](docs/spec-sync.md) · Copy protection: [docs/security-review.md](docs/security-review.md)

## The apps

| App | Platform | Folder | Who uses it |
|-----|----------|--------|-------------|
| Mesob Player | Android 7+ phones and Android TV | `android/player` | Customers |
| Mesob Player | Windows 10 (2004+) / 11, x64 | `windows/Mesob.Player` | Customers |
| Mesob Studio Control | Windows 11 (locked-down PC) | `windows/Mesob.Studio` | The content creator |
| Mesob Shop | Android tablet (12+ recommended) | `android/shop` | Distributors |
| Vendor Console (`mesob-lab`) | Windows command line | `tools/mesob-cli` | Us (the vendor) |
| Shared security core | Rust, inside every app | `core/mesob-core`, `core/mesob-ffi` | — |

## Build

Tools: Rust (rustup), .NET 8 SDK, JDK 17+, Android SDK + NDK 27, `cargo-ndk`.

```powershell
# 1. Core and tests (lab build: development vendor key)
cargo test --workspace --features mesob-ffi/lab
cargo build --release -p mesob-ffi -p mesob-cli --features mesob-ffi/lab

# 2. Lab vendor key (once) — never use for production
.\target\release\mesob-lab.exe vendor-init --dir lab\vendor

# 3. Windows apps and the end-to-end system test
dotnet build windows\Mesob.Player\Mesob.Player.csproj -c Release
dotnet build windows\Mesob.Studio\Mesob.Studio.csproj -c Release
dotnet run --project windows\Mesob.SystemTest -c Release

# 4. Android apps
.\core\mesob-ffi\build-android.ps1
cd android; .\gradlew.bat assembleRelease
```

Production builds: set `MESOB_VENDOR_PUB` (the real vendor root public key, 66 hex characters)
before building the core and omit `--features lab`. Without either, the core refuses to compile.

## Vendor Console (lab)

```powershell
mesob-lab vendor-certify  --vendor lab\vendor --request studio-request.txt --studio-id 1 --max-shops 20 --out studio.cert
mesob-lab vendor-capacity --vendor lab\vendor --studio-id 1 --block-id 1 --start 1000000 --count 50000 --out capacity-1.bin
```

Studio Control creates the studio key and saves `studio-request.txt` (Studio setup). The vendor
returns `studio.cert` and capacity blocks, which Studio Control imports.

## A day with Mesob

1. **Studio** encrypts titles, sets prices, sets up shop tablets from their setup requests, makes
   credit packs, and writes a sync stick per shop. Any title can be played or opened from the
   Titles page (Play button or click the row) to check which file is which; it is decrypted in
   memory with the studio's own key and nothing is written to disk.
2. **Shop** imports the stick (titles, locked credit packs, signed catalog). The distributor pays
   the studio's collector; the studio sends an unlock code by SMS; the shop types it in.
3. **Customer** shops like on Kindle or Amazon, but over the shop's own Wi-Fi with no internet:
   join the Wi-Fi, scan the shop's QR, search and filter the catalog, **add titles to a cart** (it
   survives closing the app) and tap *Place order*. The phone shows a short **pickup code** such as
   `K472` and the total. At the counter the customer pays cash; the cashier types the code on the
   tablet, checks the items, and taps *Paid*. Only then are licenses signed, sealed to that phone,
   and the titles download straight into the library. Editing the cart and ordering again replaces
   the waiting order; titles already owned drop out of the cart. The same store runs in the Windows
   Player. Without Wi-Fi: show the phone's request QR at the counter and scan the license QR back,
   or (PC) use a USB request file. The checkout follows MediaVault's in-shop flow; see
   `docs/spec-sync.md`.
4. **Evening:** the shop sends its daily report code by SMS. On the next visit the sync stick takes
   the signed sales log back; Studio Control checks it and updates the accounts.

## Folder layout

```
core/mesob-core      formats, signatures, sealing, decryption, codes, sales log (+ tests)
core/mesob-ffi       C ABI (Windows) and JNI (Android) bridge, JSON operations (+ flow test)
tools/mesob-cli      mesob-lab: vendor console and lab toolkit
windows/             Mesob.Interop (shared), Mesob.Player, Mesob.Studio, Mesob.SystemTest
android/             core (JNI + Keystore), player (phone + TV), shop (tablet)
docs/                specs and the security review
lab/                 development keys — git-ignored, never shipped
```
