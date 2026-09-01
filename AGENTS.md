INFO ./docs/*

# Wand Enhancer Agent Notes

This repository patches the Wand Electron app from a .NET Framework WPF desktop tool. Keep changes narrow and preserve the patch pipeline invariants.

## Patch behavior

- This fork intentionally omits the Remote Web Panel, renderer-script injection, and all LAN HTTP/WebSocket listeners. Do not reintroduce those capabilities.
- Pro activation rewrites account-returning service methods and the `ACTION_SET_ACCOUNT` reducer. If a future Wand build changes these method bodies, re-derive the regexes against the live `app-*.bundle.js`.

## ASAR Patch Pipeline

- Preserve and restore both `resources/app.asar` and `resources/app.asar.unpacked` backups.
- Do not commit extracted `.source/` or `.sources/` output. Recreate it only for reverse-engineering sessions.
- `AsarSharp.AsarExtractor.ExtractAll` must skip unpacked entries when their source path equals the destination (in-place extraction is a self-copy that fails on locked files like `TrainerLib_x64.dll`) and silently skip unpacked entries whose source is missing on disk (e.g. `auxiliary/GameLauncher.exe` removed by an installer). Do not reintroduce hard failure on either case.
- The `DevToolsOnF12` patch anchors on the Electron main-process `<app>.whenReady().then(` site and attaches a `before-input-event` hook to every `BrowserWindow.webContents`. Do not patch the renderer keydown listener — the minified `ACTION_OPEN_DEV_TOOLS` dispatch site is not stable across Wand releases.

## Validation

- Build with `build.ps1 -Configuration Release` on Windows and verify the generated patcher applies and restores both ASAR paths correctly.
