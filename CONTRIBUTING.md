# Contributing to HandheldGyroDSU

Thank you for your interest in contributing to **HandheldGyroDSU**! Contributions for new handheld devices, motion controllers, emulator auto-configuration presets, and UI translations are warmly welcome.

---

## 🛠️ Development Setup

1. **Prerequisites**:
   - Windows 10 (Build 19041+) or Windows 11 (x64)
   - [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. **Build & Verify**:
   ```powershell
   dotnet build .\HandheldGyroDSU.sln -c Release
   dotnet run --project .\HandheldDsuServer.csproj -- --self-test
   ```
   All pull requests must build with **`0 Warning(s)`** and **`0 Error(s)`** and pass the `--self-test` suite.

---

## 🌍 Adding a New Language (`Locales/*.json`)

HandheldGyroDSU uses a 100% JSON-driven localization system:

1. Copy [`HandheldDsuWidget/Locales/en.json`](HandheldDsuWidget/Locales/en.json) to `HandheldDsuWidget/Locales/<lang_code>.json` (for example, `de.json`, `es.json`, `ja.json`).
2. Update the metadata keys at the top of the file:
   ```json
   {
     "@@locale": "de",
     "@@languageName": "Deutsch (DE)",
     ...
   }
   ```
3. Translate the string values while keeping `{0}`, `{1}` format placeholders intact.
4. Build the project—your new language will automatically be embedded into the single-file `.exe` and appear in the **Settings → Interface Language** dropdown!

---

## 🎮 Adding Controller `VID:PID` Signatures

Known motion-capable controllers and handheld gamepads are identified in [`HandheldDsuWidget/Services/ControllerAndAppWatcherService.cs`](HandheldDsuWidget/Services/ControllerAndAppWatcherService.cs). When submitting a PR for a new controller:
- Provide the USB/Bluetooth `VendorId` and `ProductId` in hexadecimal.
- Specify whether the controller has a native onboard IMU or pairs with the handheld's internal WinRT IMU.

---

## 📏 Code & Commit Guidelines

Please review [`AGENTS.md`](AGENTS.md) before opening a pull request:
- **100% English Codebase**: All C#/XAML identifiers, comments, XML docstrings, and hardware names must be in English.
- **No Hardcoded UI Text**: Never hardcode user-facing strings in `.cs` or `.xaml`; always use `LocalizationService.Instance`.
- **Conventional Commits**: Format commit messages as `feat(scope): ...`, `fix(scope): ...`, `docs(scope): ...`, or `refactor(scope): ...`.
