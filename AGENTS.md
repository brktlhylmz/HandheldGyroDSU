# AGENTS.md — Engineering, Architecture & Localization Guidelines

This repository (**HandheldGyroDSU**) is a global open-source project targeting Windows handhelds (ASUS ROG Ally, ROG Ally X, Lenovo Legion Go, MSI Claw, Ayaneo, GPD) and motion-capable controllers (Sony DualSense, DualShock 4, Switch Pro, 8BitDo, GameSir).

All AI coding agents and contributors **MUST** strictly follow the rules below when writing, modifying, or releasing code in this repository.

---

## 1. 100% English Codebase & Comments (Global Standard)

- **Source Code & Identifiers**: All namespaces, classes, methods, properties, variables, enums, and constants **must be written in English**.
- **Code Comments & XML Documentation**: All inline comments (`// ...`), block comments, and XML docstrings (`/// <summary>`) in `.cs`, `.xaml`, and `.csproj` files **must be written strictly in English**. Never write Turkish comments in source code.
- **Hardware & Protocol Strings**: Hardware device names (e.g., `"ASUS ROG Ally Controller"`, `"Lenovo Legion Go Controller"`, `"Sony DualSense Wireless Controller"`, `"Xbox / XInput Controller"`), protocol constants, log messages, and default profile names **must be in English**.
- **Git Commit Messages**: All commit messages must follow Conventional Commits in **English** (e.g., `feat(ui): ...`, `fix(protocol): ...`, `feat(rpcs3): ...`).

---

## 2. Zero Hardcoded UI Strings (Strict Localization via `Locales/*.json`)

- **Never hardcode Turkish or any single-language UI text inside `.cs` or `.xaml` files.**
- All user-facing text (labels, tooltips, badges, toast/snackbar notifications, error messages) **must** be retrieved from `LocalizationService.Instance` (`_loc["Key"]` or `_loc.Format("KeyFormat", ...)` in C#, and `{Binding [Key], Source={x:Static services:LocalizationService.Instance}}` in XAML).
- Whenever a new UI string is introduced, add its key to **both**:
  - `HandheldDsuWidget/Locales/en.json`
  - `HandheldDsuWidget/Locales/tr.json`

---

## 3. Codebase Structure & Partial Class Modularity

The solution (`HandheldGyroDSU.sln`) consists of two projects:

1. **`HandheldDsuServer` (Root Project)**:
   - Low-latency 250 Hz Cemuhook DSU v1001 UDP server (`DsuUdpServer.cs`), Named Pipe IPC server (`IpcPipeServer.cs`), physical sensor providers (`WindowsSensorProvider.cs`, `DualSenseHidSensorProvider.cs`, `DualShock4HidSensorProvider.cs`, `SwitchProHidSensorProvider.cs`), 1-Euro + Complementary sensor fusion (`MotionFilterPipeline.cs`), and profile persistence (`ProfileRepository.cs`).
2. **`HandheldDsuWidget` (WPF Companion & Overlay UI)**:
   - **`MainWindow` Partial Classes**: Never dump all UI logic into a single file. Keep code organized across the designated partial files:
     - `MainWindow.xaml.cs`: Window lifecycle, IPC polling, system tray, window pinning (`SetWindowPos`), snackbar/HUD toast dispatch, and gamepad focus list (`GetActiveTabNavigableControls`).
     - `MainWindow.Dashboard.cs`: Live 250 Hz crosshair radar, axis toggles, sensitivity/deadzone sliders, calibration, and tabletop guard.
     - `MainWindow.Automation.cs`: Connected controller cards, custom controller renaming, per-controller Recenter Hotkey binding, and emulator/app auto-profile rules (including running app quick-picker).
     - `MainWindow.Profiles.cs`: Profile CRUD, import/export, and per-profile **Gyro-to-Mouse** configuration (`GyroMouseService`).
     - `MainWindow.Settings.cs`: UDP port management, startup/tray toggles, GitHub Release channel & one-click updater (`UpdateService`), Developer Options simulator/loopback tools, and **RPCS3 Handheld SDL Gyro & Axis Patcher** (`Rpcs3SdlPatcherService`).

---

## 4. Architecture, Driverless Philosophy & UX Principles

- **Strictly Driverless Philosophy**:
  - Never require or bundle invasive kernel drivers (`ViGEmBus`, `HidHide`) that hide physical controllers or conflict with OEM software (`Armoury Crate`, `Legion Space`, `Steam Input`).
  - For emulators with native DSU support (Cemu, Yuzu, Sudachi, Citron, Ryujinx, Dolphin, Vita3K, PCSX2), stream motion via UDP (`26760` / `26762`).
  - For RPCS3 (which lacks a native DSU client), `Rpcs3SdlPatcherService` uses dynamic x64 pattern scanning (`B9 00 30 01 00 FF 15` -> `B9 20 B0 01 00`) to enable `SDL_INIT_SENSOR | SDL_INIT_VIDEO` on RPCS3's main thread so handheld motion works natively through RPCS3's built-in `SDL` handler.
  - For PC/Steam games, `GyroMouseService` translates gyro motion into 250 Hz relative mouse input (`SendInput`) per profile and **automatically pauses cursor injection whenever the HandheldGyroDSU window is in the foreground**.
- **Navigation & Layout**:
  - Maintain the left vertical Navigation Rail (`Dashboard`, `Controllers`, `Profiles`, `Settings`) and responsive card layout. Avoid cramped multi-button rows or horizontal text clipping.
- **Zero-Config Multi-Controller DSU**:
  - DSU slots (`0..3`) and secondary cluster UDP ports (`26762` for 5–8 controllers) are managed automatically in the background based on connected controllers. Do not expose raw technical slot buttons in the UI.
- **Gamepad & Fullscreen Friendliness**:
  - Every view must remain navigable via gamepad (`LB`/`RB` for Navigation Rail tabs, D-Pad/Left Stick for focus, `A` to toggle/activate, `Y` to calibrate).
  - **Crucial**: Whenever you add a new button, toggle, slider, or combo box to any tab in `MainWindow.xaml`, register it in `GetActiveTabNavigableControls()` inside `MainWindow.xaml.cs`.
  - Background notifications must use the click-through `HudToastService` (`WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`) so fullscreen games never lose focus.

---

## 5. Branching Strategy, Release Channels & Verification

- **`dev` Branch (Beta / Pre-Release Channel)**:
  - Develop new features on feature branches off `dev` (e.g., `feat/rpcs3-sdl-gyro-fix`) and merge them into `dev`.
  - Pushing to `dev` triggers `.github/workflows/build-and-release.yml` to publish a `-beta` pre-release on GitHub Releases (skipped automatically if `dev` points to the exact same commit SHA as `master`).
- **`master` Branch (Stable Release Channel)**:
  - Merge `dev` into `master` only when features are verified and ready for stable users.
- **Single-Instance Mutex & Local Testing**:
  - `HandheldGyroDSU.exe` enforces a single-instance mutex (`App.xaml.cs`). Before building or launching a newly compiled executable locally, always stop any running `HandheldGyroDSU` process (`Get-Process HandheldGyroDSU -ErrorAction SilentlyContinue | Stop-Process -Force`).
  - Always verify `dotnet build .\HandheldGyroDSU.sln -c Release` succeeds with **`0 Warning(s)`** and **`0 Error(s)`** before committing.
