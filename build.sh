#!/usr/bin/env bash
# Builds MdView.
#   VERSION=1.2.0 ./build.sh
# Output in out/:  MdView-Setup-<v>.exe, MdView-<v>-portable.zip, SHA256SUMS.txt
# Needs mono-devel (mcs + .NET 4.8 reference assemblies), nsis and zip:
#   sudo apt-get install mono-devel nsis zip
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${VERSION:-0.0.0}"
if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then echo "VERSION must be x.y.z, got '$VERSION'" >&2; exit 1; fi
A=/usr/lib/mono/4.8-api

rm -rf dist out obj && mkdir -p dist out obj
cat > obj/BuildInfo.cs <<CS
[assembly: System.Reflection.AssemblyVersion("$VERSION.0")]
[assembly: System.Reflection.AssemblyFileVersion("$VERSION.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("$VERSION")]
CS

cp lib/*.dll dist/ && cp -r lib/runtimes dist/ && cp -r src/web dist/ && cp README.md LICENSE dist/
# Compile against the real .NET Framework 4.8 reference assemblies (not Mono's runtime libs),
# otherwise types like Stack<T> bind to the wrong assembly and fail on Windows.
mcs -sdk:4.8 -nostdlib -lib:$A -lib:$A/Facades -target:winexe -platform:anycpu -optimize+ -langversion:7 \
  -out:dist/MdView.exe -win32icon:src/mdview.ico \
  -r:$A/mscorlib.dll -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
  -r:System.Web.Extensions.dll -r:lib/Microsoft.Web.WebView2.Core.dll -r:lib/Microsoft.Web.WebView2.WinForms.dll \
  src/MdView.cs obj/BuildInfo.cs

makensis -V2 -DVERSION="$VERSION" -DBUILD="../dist" -DOUTDIR="../out" installer/MdView.nsi
(cd dist && zip -qr "../out/MdView-$VERSION-portable.zip" .)
(cd out && sha256sum MdView-Setup-$VERSION.exe MdView-$VERSION-portable.zip > SHA256SUMS.txt)
echo "Built MdView $VERSION:"; ls -l out
