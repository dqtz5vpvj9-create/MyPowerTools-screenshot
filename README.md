# Screenshot

MyPowerTools launches an existing screenshot program and leaves the capture UI there.

- Optional on Windows, macOS and Linux: [快咔 / Kuaika](https://www.kuaika.app/), selected in Screenshot settings. Capture invokes the documented `gui` command. The default remains Snow Shot on Windows/macOS and Flameshot on Linux, preserving existing settings. Kuaika is discovered in standard install locations or configured with an absolute executable path (`kuaika.exe` / `快咔.exe`, a macOS app bundle, or a Linux `kuaika` binary/AppImage). The download button opens the official website; Kuaika's current release API does not publish a SHA-256 checksum, so it does not use Snow Shot's verified automatic installer.

- Windows and macOS: [Snow Shot](https://github.com/mg-chao/snow-shot) (`snow_shot.exe`, or `snowshot.exe` on older builds)
- Linux: [Flameshot](https://github.com/flameshot-org/flameshot) (`flameshot`, capture via `flameshot gui`)

Snow Shot and Flameshot are GPL-3.0. This module does not link or vendor screenshot programs. It looks up a local install, can start it when the module starts, and stops only the process it started itself. Capture shortcuts stay registered by the selected program. Snow Shot has no capture CLI on current Windows/macOS builds (`--help` is silent; a second instance only shows the main window). **立即截图** therefore ensures Snow Shot is running, then sends the screenshot hotkey from Snow Shot's own `portable/config.json` (`global_shortcuts.screenshot`, for example `Alt+A`).

## Local image compression

The Screenshot page accepts multiple PNG, JPEG, WebP or BMP files and encodes WebP (default quality 85), JPEG or lossless PNG. An optional longest-edge limit resizes without upscaling; zero preserves dimensions. EXIF orientation is applied before encoding. WebP/PNG retain alpha; JPEG flattens onto white. Animation and oversized input (128 MB or 64 million pixels) are rejected before pixel decoding.

Files remain local. Originals are preserved; smaller results are written beside them as `name-compressed.ext`, with numbered suffixes on collision. Larger/equal results are skipped. The UI reports individual failures and before/after sizes, continues the remaining batch, and supports cancellation. PNG ignores the quality control. Metadata is not copied to outputs.

On macOS, **立即截图** sends the same hotkey through the MyPowerTools `keyboard.shortcut` platform service (CoreGraphics `CGEvent`), so MyPowerTools needs Accessibility permission (System Settings › Privacy & Security › Accessibility). The hotkey is read from `~/Library/Application Support/SnowShot/snow_shot/config.json`; without a saved value Snow Shot's macOS default `Control+1` is used. Qt writes Command as `Ctrl` and Control as `Meta` on macOS; the module swaps them into MyPowerTools notation.

Snow Shot is never downloaded automatically. When it is missing, the Screenshot page shows a **下载并安装 Snow Shot** button (command `screenshot.install`). Clicking it downloads the official build from `https://snowshot.top/setup/` (`snow-shot_macos-arm64.dmg` on Apple silicon Macs, `snow-shot_windows-x64-portable.zip` on Windows), checks the publisher's per-asset `.sha256` file, installs it for the current user (`~/Applications/Snow Shot.app`, `%LOCALAPPDATA%\Programs\Snow Shot`) and starts it. The checksum filename must match the stable alias or the actual downloaded filename from the response attachment header or final redirect URL. The aggregate `SHA256SUMS` is not used because it can lag behind release redirects. Snow Shot publishes no Intel Mac build, so the button is hidden there. On Windows the usual package is `winget install --id mg-chao.snow-shot -e`. A 4K or multi-monitor display that renders poorly should use Snow Shot's fixedRuntime build, then set that executable path in Screenshot settings.

Build from the repository root:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\tools\screenshot\build.ps1 -MyPowerToolsRepoRoot .
```

Run the offline installation and lifecycle regressions from the repository root:

```powershell
dotnet run --project tools/screenshot/tests/Screenshot.Lifecycle.Regression -c Release
```

The tests cover versioned download names, stale aggregate checksums, wrong-release rejection,
hash mismatch, download timeouts, preserved settings, rollback, and shutdown during installation.
