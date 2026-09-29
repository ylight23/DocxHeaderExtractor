#!/usr/bin/env bash
# Provisions the ONE extraction environment the V5 31-pack measurement cohort was frozen on
# (artifacts/v5-provider-cohort-31/preflight/environment.v1.json). Ubuntu 24.04 x64 only.
#
# This script is provenance, not the gate. The gate is hash equality: `dhx-v5-qualify
# --cohort31-execute` rebuilds all 31 requests and refuses unless every source-universe hash,
# semantic request hash, provider request hash and body byte matches the frozen cohort. Why the
# fonts matter: SRC-089 references TimesNewRomanPSMT without embedding it, so PdfPig's atom
# bounding boxes (part of every sourceId) depend on the installed Times New Roman files.
#
# Font files are downloaded and verified here, never committed.
set -euo pipefail

need_sha() { echo "$2  $1" | sha256sum -c --quiet - || { echo "sha256 mismatch: $1" >&2; exit 1; }; }
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

apt-get install -y dotnet-sdk-10.0 cabextract

# net9.0 runtimes (the projects target net9.0; Ubuntu 24.04 ships only the 10.0 SDK/runtime).
V=9.0.20
curl -sSL -o "$work/rt.nupkg"  "https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.linux-x64/$V/microsoft.netcore.app.runtime.linux-x64.$V.nupkg"
curl -sSL -o "$work/asp.nupkg" "https://api.nuget.org/v3-flatcontainer/microsoft.aspnetcore.app.runtime.linux-x64/$V/microsoft.aspnetcore.app.runtime.linux-x64.$V.nupkg"
need_sha "$work/rt.nupkg"  8423ef69edeaf3e7d29cfa955965e4ce266cae4013532d9b70b1068d4282e629
need_sha "$work/asp.nupkg" 965ea2b0f35c5076c2870b41121faf222e802350cc7fe7116b193fabe7792328
(cd "$work" && mkdir rt asp && unzip -qo rt.nupkg -d rt && unzip -qo asp.nupkg -d asp)
mkdir -p "/usr/lib/dotnet/shared/Microsoft.NETCore.App/$V" "/usr/lib/dotnet/shared/Microsoft.AspNetCore.App/$V"
cp "$work"/rt/runtimes/linux-x64/lib/net9.0/* "$work"/rt/runtimes/linux-x64/native/* "/usr/lib/dotnet/shared/Microsoft.NETCore.App/$V/"
cp "$work"/asp/runtimes/linux-x64/lib/net9.0/* "/usr/lib/dotnet/shared/Microsoft.AspNetCore.App/$V/"

# Times New Roman from the Microsoft core fonts distribution (times32.exe).
curl -sSL -o "$work/times32.exe" https://downloads.sourceforge.net/corefonts/times32.exe
need_sha "$work/times32.exe" db56595ec6ef5d3de5c24994f001f03b2a13e37cee27bc25c58f6f43e8f807ab
cabextract -q -d "$work/tnr" "$work/times32.exe"
dest=/usr/share/fonts/truetype/msttcorefonts
mkdir -p "$dest"
for f in Times.TTF Timesbd.TTF Timesbi.TTF Timesi.TTF; do cp "$work/tnr/$f" "$dest/$(echo "$f" | tr 'A-Z' 'a-z')"; done
need_sha "$dest/times.ttf"   4e98adeff8ccc8ef4e3ece8d4547e288ff85fdc9c7ca711a4599c234874bbe86
need_sha "$dest/timesbd.ttf" 4357b63cef20c01661a53c5dae70ffd20cb4765503aaed6d38b17a57c5a90bff
need_sha "$dest/timesbi.ttf" 192e1b0d18e90334e999a99f8c32808d6a2e74b3698b8cd90c943c2249a46549
need_sha "$dest/timesi.ttf"  c25ae529b4cecdbca148b6ccb862ee0abad770af8b1fd29c8dba619d1b8da78a
fc-cache -f >/dev/null 2>&1 || true
echo "V5 cohort31 environment provisioned. The execute mode still re-verifies every frozen hash before any call."
