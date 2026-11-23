# MdView

A lightweight Markdown viewer for Windows.

## Install

1. Run **MdView-Setup-1.1.0.exe** and approve the admin prompt. It installs to `C:\Program Files\MdView`.
2. If another app already owns `.md` files, right-click any `.md` file > **Open with** > **Choose another app** > **MdView** > **Always**.

Requires the Microsoft Edge WebView2 Runtime (built into Windows 11 and up-to-date Windows 10).

## Features

- GitHub-flavoured Markdown: tables, task lists, strikethrough, `> [!NOTE]` alerts
- Syntax highlighting for code blocks, with a copy button
- Mermaid diagrams
- Table of contents sidebar with heading filter
- Light / dark / follow-Windows theme
- Live reload when the file changes on disk
- Relative links to other `.md` files open in the same window; relative images work
- Front matter (YAML at the top of e.g. `SKILL.md`) is shown collapsed

## Shortcuts

| Key | Action |
|-----|--------|
| `Ctrl+O` | Open file |
| `F5` | Reload |
| `Ctrl+B` | Toggle contents sidebar |
| `Ctrl+Shift+L` | Cycle theme |
| `Ctrl+F` | Find |
| `Ctrl+P` | Print / save as PDF |
| `Ctrl` + `+` / `-` / `0` | Zoom |
| `Alt+Left` | Back (after following a link) |
| `Ctrl+W` | Close |

You can also drop a `.md` file onto the window.

## Uninstall

Settings > Apps > Installed apps > **MdView** > Uninstall.

Settings and the log file (`mdview.log`) live in `%LOCALAPPDATA%\MdView`.

## Development

```
src/MdView.cs        WinForms + WebView2 host (C# 7, .NET Framework 4.8)
src/web/             Renderer: viewer.html / viewer.css / viewer.js + bundled libs
lib/                 WebView2 SDK assemblies and native loaders (1.0.3856)
installer/           NSIS script for MdView-Setup.exe (Program Files, HKLM)
scripts/             Older per-user install scripts (no admin needed)
build.sh             Builds dist/ and the installer (Linux: mono-devel + nsis)
```

Bundled libraries: marked 15, highlight.js 11, Mermaid 11, DOMPurify 3, WebView2 SDK.

The renderer can be tried in a browser on its own: open `src/web/viewer.html` and call
`MdView.load(name, markdownText)` from the console.

Set `MDVIEW_DEVTOOLS=1` before starting MdView to enable F12 DevTools.
