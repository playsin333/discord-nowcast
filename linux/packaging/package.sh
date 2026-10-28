#!/usr/bin/env bash
# Builds the Linux release files into dist/: the program for amd64 and arm64,
# .deb and .rpm for both, an AppImage for x86_64 and the PKGBUILD for the AUR.
# Run from the repository root: linux/packaging/package.sh [version]. The
# version is src/Program.cs's unless given. Needs Go, nfpm and, for the
# AppImage, curl (appimagetool is downloaded); NO_APPIMAGE=1 skips it.
set -euo pipefail

version=${1:-$(grep -oP 'const string Version = "\K[^"]+' src/Program.cs)}
[ -n "$version" ] || { echo "no version" >&2; exit 1; }
dist=$(pwd)/dist
mkdir -p "$dist"

for arch in amd64 arm64; do
  (cd linux && CGO_ENABLED=0 GOOS=linux GOARCH=$arch go build -trimpath -ldflags "-s -w -X main.version=$version" -o "$dist/discord-nowcast-linux-$arch" .)
  # nfpm takes the program from one place: dist/nfpm, for each architecture in turn.
  mkdir -p "$dist/nfpm"
  cp "$dist/discord-nowcast-linux-$arch" "$dist/nfpm/discord-nowcast"
  for packager in deb rpm; do
    case $packager/$arch in
      deb/*) target=$dist/discord-nowcast_$arch.deb ;;
      rpm/amd64) target=$dist/discord-nowcast.x86_64.rpm ;;
      rpm/arm64) target=$dist/discord-nowcast.aarch64.rpm ;;
    esac
    NFPM_ARCH=$arch VERSION=$version nfpm package --config linux/packaging/nfpm.yaml --packager "$packager" --target "$target"
  done
done
rm -r "$dist/nfpm"

# AppImage: the program, its .desktop file and icon. The program needs no
# libraries, so AppRun is the program itself.
if [ "${NO_APPIMAGE:-}" != 1 ]; then
  appdir=$(mktemp -d)/discord-nowcast.AppDir
  mkdir -p "$appdir/usr/bin"
  cp "$dist/discord-nowcast-linux-amd64" "$appdir/usr/bin/discord-nowcast"
  ln -s usr/bin/discord-nowcast "$appdir/AppRun"
  cp linux/packaging/discord-nowcast.desktop "$appdir/"
  cp assets/icon.png "$appdir/discord-nowcast.png"
  tool=$(mktemp -d)/appimagetool
  curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/1.9.0/appimagetool-x86_64.AppImage
  chmod +x "$tool"
  ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 VERSION=$version "$tool" --no-appstream "$appdir" "$dist/discord-nowcast-x86_64.AppImage"
fi

sum() { sha256sum "$1" | cut -d' ' -f1; }
sed -e "s/@VERSION@/$version/" \
    -e "s/@SHA_DESKTOP@/$(sum linux/packaging/discord-nowcast.desktop)/" \
    -e "s/@SHA_ICON@/$(sum assets/icon.png)/" \
    -e "s/@SHA_LICENSE@/$(sum LICENSE)/" \
    -e "s/@SHA_AMD64@/$(sum "$dist/discord-nowcast-linux-amd64")/" \
    -e "s/@SHA_ARM64@/$(sum "$dist/discord-nowcast-linux-arm64")/" \
    linux/packaging/aur/PKGBUILD.in > "$dist/PKGBUILD"

ls -l "$dist"
