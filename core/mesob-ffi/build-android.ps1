# Build the Rust core for Android and copy it into the shared Android module.
# Needs: rustup targets aarch64-linux-android armv7-linux-androideabi x86_64-linux-android, cargo-ndk, Android NDK.
# Production builds: set $env:MESOB_VENDOR_PUB to the vendor root public key (hex) first.
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { "$env:LOCALAPPDATA\Android\Sdk" }
if (-not $env:ANDROID_NDK_HOME) {
    $ndk = Get-ChildItem "$sdk\ndk" -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $ndk) { throw "Android NDK not found under $sdk\ndk" }
    $env:ANDROID_NDK_HOME = $ndk.FullName
}
$out = Join-Path $root "android\core\src\main\jniLibs"
# Without a vendor key this is a LAB build (development keys only, never ship it).
$features = @()
if (-not $env:MESOB_VENDOR_PUB) { $features = @("--features", "lab"); Write-Warning "LAB BUILD: MESOB_VENDOR_PUB is not set." }
Push-Location $root
try {
    cargo ndk -t arm64-v8a -t armeabi-v7a -t x86_64 -P 24 -o $out build --release -p mesob-ffi @features
    if ($LASTEXITCODE -ne 0) { throw "cargo ndk failed" }
} finally { Pop-Location }
Get-ChildItem $out -Recurse -Filter *.so | ForEach-Object { "{0}  {1:N0} bytes" -f $_.FullName, $_.Length }
