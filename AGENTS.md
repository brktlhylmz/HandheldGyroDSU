# AGENTS.md — Engineering & Localization Guidelines

This repository (**HandheldGyroDSU**) is a global open-source project targeting Windows handhelds (ASUS ROG Ally, Lenovo Legion Go, MSI Claw, Ayaneo, GPD) and motion-capable controllers (Sony DualSense, DualShock 4, Switch Pro, 8BitDo, GameSir).

All AI coding agents and contributors **MUST** strictly follow the rules below when writing or modifying code in this repository.

---

## 1. 100% English Codebase & Comments (Global Standard)

- **Source Code & Identifiers**: All namespaces, classes, methods, properties, variables, enums, and constants **must be written in English**.
- **Code Comments & XML Documentation**: All inline comments (`// ...`), block comments, and XML docstrings (`/// <summary>`) in `.cs`, `.xaml`, and `.csproj` files **must be written strictly in English**. Never write Turkish comments in source code.
- **Hardware & Protocol Strings**: Hardware device names (e.g., `"ASUS ROG Ally Controller"`, `"Lenovo Legion Go Controller"`, `"Sony DualSense Wireless Controller"`, `"Xbox / XInput Controller"`), protocol constants, log messages, and default profile names **must be in English**.
- **Git Commit Messages**: All commit messages must follow Conventional Commits in **English** (e.g., `feat(ui): ...`, `fix(protocol): ...`, `refactor(localization): ...`).

---

## 2. Zero Hardcoded UI Strings (Strict Localization via `Locales/*.json`)

- **Never hardcode Turkish or any single-language UI text inside `.cs` or `.xaml` files.**
- All user-facing text (labels, tooltips, badges, toast/snackbar notifications, error messages) **must** be retrieved from `LocalizationService.Instance` (`_loc["Key"]` or `_loc.Format("KeyFormat", ...)` in C#, and `{Binding [Key], Source={x:Static services:LocalizationService.Instance}}` in XAML).
- Whenever a new UI string is introduced, add its key to **both**:
  - `HandheldDsuWidget/Locales/en.json`
  - `HandheldDsuWidget/Locales/tr.json`

---

## 3. Architecture & UX Principles

- **Navigation & Layout**: Maintain the left vertical Navigation Rail (`Dashboard`, `Controllers`, `Profiles`, `Settings`) and responsive card layout. Avoid cramped multi-button rows or horizontal text clipping.
- **Zero-Config Multi-Controller DSU**: DSU slots (`0..3`) and secondary cluster UDP ports (`26762` for 5–8 controllers) are managed automatically in the background based on connected controllers. Do not expose raw technical slot buttons in the UI.
- **Gamepad & Fullscreen Friendliness**: Every view must remain navigable via gamepad (`LB`/`RB` for Navigation Rail tabs, D-Pad/Left Stick for focus, `A` to toggle/activate, `Y` to calibrate), and background notifications must use the click-through `HudToastService` (`WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`) so fullscreen games never lose focus.
- **Incremental Verification & Commits**: Build and verify (`dotnet build .\HandheldGyroDSU.sln`) with `0 Warning(s)` and `0 Error(s)` before committing each logical step.
