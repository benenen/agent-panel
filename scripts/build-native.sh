#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
if [[ $(uname -s) != Linux || $(uname -m) != x86_64 ]]; then
  echo 'The initial native build supports Linux x86_64.' >&2
  exit 1
fi
revision=c3203ea4b169a18eb2ccfe92847e426d8afea858
deps="$root/.deps"
source_dir="${GHOSTTY_SOURCE:-$deps/ghostty}"
mkdir -p "$deps" "$root/native/out"
zig_bin="${ZIG:-zig}"
if ! command -v "$zig_bin" >/dev/null || [[ $("$zig_bin" version) != 0.16.* ]]; then
  zig_bin="$deps/zig-x86_64-linux-0.16.0/zig"
  if [[ ! -x "$zig_bin" ]]; then
    curl -fL --retry 3 https://ziglang.org/download/0.16.0/zig-x86_64-linux-0.16.0.tar.xz -o "$deps/zig.tar.xz"
    echo "70e49664a74374b48b51e6f3fdfbf437f6395d42509050588bd49abe52ba3d00  $deps/zig.tar.xz" | sha256sum -c -
    tar -xJf "$deps/zig.tar.xz" -C "$deps"
  fi
fi
if [[ ! -d "$source_dir/.git" ]]; then
  git init "$source_dir"
  git -C "$source_dir" remote add origin https://github.com/ghostty-org/ghostty.git
  git -C "$source_dir" fetch --depth 1 origin "$revision"
  git -C "$source_dir" checkout --detach FETCH_HEAD
fi
if [[ $(git -C "$source_dir" rev-parse HEAD) != "$revision" ]]; then
  echo "Ghostty must be at pinned revision $revision" >&2; exit 1
fi
export ZIG_GLOBAL_CACHE_DIR="${ZIG_GLOBAL_CACHE_DIR:-$deps/zig-cache}"
# curl handles HTTP proxy environments that Zig's fetch client cannot use.
# Zig verifies each archive's content hash against the pinned dependency.
fetch() {
  local name=$1 url=$2 expected=$3
  if [[ ! -f "$deps/$name.tar.gz" ]]; then
    curl -fL --retry 3 "$url" -o "$deps/$name.tar.gz.tmp"
    mv "$deps/$name.tar.gz.tmp" "$deps/$name.tar.gz"
  fi
  local actual
  actual=$(cd "$source_dir" && "$zig_bin" fetch "$deps/$name.tar.gz")
  [[ "$actual" == "$expected" ]] || { echo "Hash mismatch: $name" >&2; exit 1; }
}
fetch uucode https://codeload.github.com/jacobsandlund/uucode/tar.gz/9d55524551411b493cca41ca06363625d90aff1e uucode-0.2.0-ZZjBPuuFVgC8YZ8eld4fOKsZANLIhTFMzULQxhkLi1C7
fetch translate-c https://codeberg.org/vancluever/translate-c/archive/4e879eb8aba615de112eabd1231ea6e01920cead.tar.gz translate_c-0.0.0-Q_BUWhVNBwDOEcIqub4VFPJPB6D9dgwzUMHTX5KWr8Xr
fetch aro https://codeload.github.com/vancluever/arocc/tar.gz/f97cdfc3779aec4b242299e2fc9a1c828c3547c6 aro-0.0.0-JSD1Qk6lNgDdcDV4Vh7Sfy-34m2TluIVOdPzMmj_0BjX
fetch themes https://deps.files.ghostty.org/ghostty-themes-release-20260928-151043-99d9701.tgz N-V-__8AAAfDBACe1jGqjr9jIG3UAK8KbzJKQ7xrzYoau-_a
fetch zlib https://deps.files.ghostty.org/zlib-1220fed0c74e1019b3ee29edae2051788b080cd96e90d56836eea857b0b966742efb.tar.gz N-V-__8AAB0eQwD-0MdOEBmz7intriBReIsIDNlukNVoNu6o
fetch highway https://deps.files.ghostty.org/highway-66486a10623fa0d72fe91260f96c892e41aceb06.tar.gz N-V-__8AAGmZhABbsPJLfbqrh6JTHsXhY6qCaLAQyx25e0XE
fetch wuffs https://deps.files.ghostty.org/wuffs-7411f488fe2e2c205c3d3b3d28638b7356522930.tar.gz N-V-__8AAP5JWgCGP_AD0teWpa4krRvE9VPZzvviGdbmN4jI
fetch pixels https://deps.files.ghostty.org/pixels-12207ff340169c7d40c570b4b6a97db614fe47e0d83b5801a932dcd44917424c8806.tar.gz N-V-__8AADYiAAB_80AWnH1AxXC0tql9thT-R-DYO1gBqTLc
cd "$source_dir"
"$zig_bin" build -Demit-lib-vt=true -Doptimize=ReleaseFast --prefix "$deps/ghostty-install"
cp -L "$deps/ghostty-install/lib/libghostty-vt.so" "$root/native/out/libghostty-vt.so"
# The soname can include a version; copy the matching files as well.
cp -L "$deps/ghostty-install/lib/"*.so.* "$root/native/out/" 2>/dev/null || true
cc -std=c11 -O2 -shared -fPIC -Wall -Wextra -Werror \
  -I"$source_dir/include" "$root/native/agent_panel_ghostty.c" \
  -L"$root/native/out" -lghostty-vt -lutil -Wl,-rpath,'$ORIGIN' \
  -o "$root/native/out/libagent_panel_ghostty.so"
echo "Native terminal libraries built in native/out. Run dotnet build next."
