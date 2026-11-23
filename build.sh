#!/usr/bin/env bash
# Builds dist/ (portable app) and installer/MdView-Setup-<version>.exe.
# Needs: mono (mcs + 4.8 reference assemblies) and nsis.  apt install mono-devel nsis
set -euo pipefail
cd "$(dirname "$0")"
A=/usr/lib/mono/4.8-api
rm -rf dist && mkdir -p dist
cp lib/*.dll dist/ && cp -r lib/runtimes dist/ && cp -r src/web dist/ && cp README.md dist/
# Compile against the real .NET Framework 4.8 reference assemblies (not Mono's runtime libs),
# otherwise types like Stack<T> bind to the wrong assembly and fail on Windows.
mcs -sdk:4.8 -nostdlib -lib:$A -lib:$A/Facades -target:winexe -platform:anycpu -optimize+ -langversion:7 \
  -out:dist/MdView.exe -win32icon:src/mdview.ico \
  -r:$A/mscorlib.dll -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
  -r:System.Web.Extensions.dll -r:lib/Microsoft.Web.WebView2.Core.dll -r:lib/Microsoft.Web.WebView2.WinForms.dll \
  src/MdView.cs
(cd installer && makensis -V2 MdView.nsi)
echo "Built dist/MdView.exe and installer/MdView-Setup-*.exe"
