## Description
<!-- Provide a clear, concise summary of the changes introduced in this PR. -->

## Related Issues / Discussions
<!-- Fixes #123 / Closes #456 -->

## Type of Change
- [ ] Bug fix (non-breaking change fixing an issue)
- [ ] New feature (non-breaking change adding functionality)
- [ ] Optimization / Refactor (no functional changes)
- [ ] Localization / Documentation update

## Checklist (per AGENTS.md Guidelines)
- [ ] **100% English Codebase**: Code, comments, variables, XML docstrings, and commit messages are in English.
- [ ] **Zero Hardcoded UI Strings**: All new user-facing strings are localized in both `HandheldDsuWidget/Locales/en.json` and `HandheldDsuWidget/Locales/tr.json`.
- [ ] **Gamepad Friendly**: Any newly added interactive controls are registered in `GetActiveTabNavigableControls()` (`MainWindow.xaml.cs`).
- [ ] **Build Verification**: `dotnet build .\HandheldGyroDSU.sln -c Release` completes with **0 Warning(s)** and **0 Error(s)**.
- [ ] **Tested on Target Hardware**: Verified on a physical handheld, motion controller, or simulated environment.
