#!/bin/sh
set -eu
cef="$1"
out="$2"
mkdir -p "$out"
src="$(dirname "$0")/../src/EnbNext.App/Native/Cef"
g++ -std=c++17 -O2 -fPIC -shared -DCEF_API_VERSION=15000 -I "$cef" "$src/Browser.cpp" "$src/TextureTransfer.cpp" -L "$cef/Release" -lcef '-Wl,-rpath,$ORIGIN' -o "$out/libEnbNext.Cef.so"
g++ -std=c++17 -O2 -DCEF_API_VERSION=15000 -I "$cef" "$src/Helper.cpp" -L "$cef/Release" -lcef -Wl,--allow-shlib-undefined '-Wl,-rpath,$ORIGIN' -o "$out/EnbNext.CefHelper"