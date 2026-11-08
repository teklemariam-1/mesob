#!/bin/bash
# Provision a lab studio + shop with the mesob-lab CLI, package demo titles and grant them to the shop.
set -e
M="$(cd "$(dirname "$0")/../.." && pwd)"
CLI=$M/target/release/mesob-lab.exe
L="$M/lab/labshop"; mkdir -p "$L"
A=$M/demo/assets
rm -rf "$L/studio" "$L/shop" "$L/titles"; mkdir -p "$L/titles"
$CLI studio-init --vendor $M/lab/vendor --dir "$L/studio" --studio-id 9
$CLI capacity --vendor $M/lab/vendor --studio "$L/studio" --block-id 1 --start 5000000 --count 1000
$CLI shop-init --studio "$L/studio" --dir "$L/shop" --shop-id 4
$CLI credit --studio "$L/studio" --shop "$L/shop" --block "$L/studio/capacity-1.bin" --pack-id 1 --start 5000000 --count 200
pkg() { # file kind en ti artist poster out
  $CLI package --studio "$L/studio" --input "$1" --out "$L/titles/$7.msb" --kind "$2" --title-en "$3" --title-ti "$4" --artist "$5" --poster "$6"
  $CLI grant --studio "$L/studio" --shop "$L/shop" --title "$L/titles/$7.msb"
}
STARLINK="${STARLINK_MP4:-$HOME/Videos/HowThingsClick-Starlink/HowThingsClick-Starlink-Short-YouTube.mp4}"
pkg "$STARLINK" video "How Things Click: Starlink" "ስታርሊንክ" "How Things Click" $A/starlink.jpg starlink
pkg "$A/starlink-audio.m4a" audio "Starlink (audio)" "ስታርሊንክ" "How Things Click" $A/starlink.jpg starlink-audio
pkg "$A/proverbs.pdf" pdf "Tigrinya Proverbs" "ምስላታት ትግርኛ" "Mesob Books" $A/proverbs.jpg proverbs
pkg "$A/asmara-nights.bin" video "Asmara Nights" "ለይቲ ኣስመራ" "Studio Seven" $A/asmara.jpg asmara-nights
pkg "$A/hade-libi.bin" audio "Hade Libi" "ሓደ ልቢ" "Studio Seven Band" $A/hadelibi.jpg hade-libi
echo DONE
