# HandheldGyroDSU

[![Build & DSU Self-Test](https://github.com/brktlhylmz/HandheldGyroDSU/actions/workflows/build-and-release.yml/badge.svg)](https://github.com/brktlhylmz/HandheldGyroDSU/actions/workflows/build-and-release.yml)
[![Download Standalone .EXE](https://img.shields.io/badge/Download-HandheldGyroDSU.exe-00E676?logo=windows&logoColor=white)](https://github.com/brktlhylmz/HandheldGyroDSU/releases/latest/download/HandheldGyroDSU.exe)
[![Latest Release](https://img.shields.io/github/v/release/brktlhylmz/HandheldGyroDSU?color=2979FF&label=Release)](https://github.com/brktlhylmz/HandheldGyroDSU/releases)
[![.NET 10](https://img.shields.io/badge/.NET-10.0%20Windows-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Protocol](https://img.shields.io/badge/Protocol-Cemuhook%20DSU%20v1001-00B0FF)](https://v1993.github.io/cemuhook-protocol/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**HandheldGyroDSU** is a zero-configuration, standalone **250 Hz Cemuhook DSU (`v1001`) motion server** and **Xbox Game Bar (`Win + G`) companion overlay** built for Windows handheld gaming PCs (**ASUS ROG Ally / Ally X**, **Lenovo Legion Go**, **MSI Claw**, **Ayaneo**, **GPD**) and external controllers (**Sony DualSense**, **DualShock 4**, **Nintendo Switch Pro**, **Xbox / XInput**, **8BitDo**, **GameSir**).

It bridges internal Windows WinRT Inertial Measurement Units (`Gyrometer` + `Accelerometer`) and connected gamepads directly to **Dolphin**, **Cemu**, **Eden**, **Yuzu**, **Sudachi**, **Ryujinx**, **Azahar / Citra**, and **RPCS3** with sub-4ms latency—shipped as a **single portable `.exe`** with zero external runtime dependencies.

---

## 📥 Direct Download (No Installation Required)

| Package | Architecture | Description | Direct Link |
| :--- | :--- | :--- | :--- |
| **`HandheldGyroDSU.exe`** | Windows 10 / 11 (`x64`) | **Standalone Single-File Executable** (Self-contained — no `.NET` install or `.zip` extraction needed) | **[⬇️ Download `HandheldGyroDSU.exe`](https://github.com/brktlhylmz/HandheldGyroDSU/releases/latest/download/HandheldGyroDSU.exe)** |
| **All Releases & Changelogs** | Windows 10 / 11 (`x64`) | Version history, release notes, and CI build artifacts | **[📦 View GitHub Releases](https://github.com/brktlhylmz/HandheldGyroDSU/releases)** |

---

## ✨ Key Features

- **⚡ 250 Hz Zero-Allocation IMU Engine**:
  - Polls hardware `Windows.Devices.Sensors.Gyrometer` and `Accelerometer` at a 4 ms interval (`250 Hz`) with stack-allocated `Span<byte>` Cemuhook DSU `v1001` UDP packet serialization and IEEE 802.3 CRC32 verification.
  - Optional **Full Gamepad Input Forwarding**: Embeds live XInput buttons, D-Pad, dual analog sticks, and analog triggers (`L2`/`R2`) directly into the DSU packet stream alongside motion data.
- **🎯 Per-Controller Quick Recenter Hotkey (Single, Combo & Handheld Macro Keys)**:
  - Assign a custom recenter shortcut per controller directly in the **Controllers** tab—supports single buttons (`R3`, `Select`), 2-button combinations (`Select + R3`, `LB + RB`), extra `RawGameController` buttons (`RawBtn`), or handheld back-paddle macro keys (`M1`/`M2` mapped to `F6..F24`, `Insert`, `Home`, etc.).
  - Re-centers pitch/roll posture and gyro bias instantaneously in-game (`POST /recenter`) with click-through HUD confirmation.
- **🛡️ Tabletop (Stationary) Calibration Guard**:
  - Prevents accidental mid-air or tilted hand calibrations from corrupting zero-rate gyro bias offsets (`±1.5°/s` clamp + `AccZ ≤ -0.82G` flat-surface check). Enabled by default and toggleable in **Settings**.
- **🎮 Custom Controller Naming & Multi-Controller Identification (Up to 8 Pads)**:
  - Automatically assigns slots `0..3` on primary UDP port **`26760`** and spawns a **Secondary DSU Cluster** on UDP port **`26762`** for controllers 5–8.
  - Automatically numbers identical controllers (`#2`, `#3`), highlights the currently pressed controller with a live **`ACTIVE INPUT`** badge, and lets you assign custom names (`✎`) to each controller.
- **🐬 1-Click Dolphin Emulator Auto-Configuration**:
  - Selecting `dolphin.exe` in the **Controllers → Emulator Auto-Switch** section automatically configures `DSUClient.ini`, generates ready-to-load profiles (`HandheldGyroDSU_Wiimote.ini`, `HandheldGyroDSU_Sideways.ini` with full `Upright + Sideways` Wii Wheel steering math, and `HandheldGyroDSU_GCPad.ini`), and binds `IMUIR` pointer + motion axes out of the box.
- **🪟 `Win + G` Xbox Game Bar Companion & Click-Through HUD**:
  - Pops up automatically alongside **Xbox Game Bar (`Win + G`)** and hides when returning to your game unless pinned (`📌`).
  - Uses a click-through `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` HUD toast overlay so fullscreen games never lose focus, and drops UI polling to **0% CPU** when minimized to the system tray.
- **🕹️ 100% Gamepad-First UI & Multi-Language Support (`en` / `tr`)**:
  - Full controller navigation (`LB`/`RB` tabs, `D-Pad`/`Left Stick` focus & sliders, `A` toggle, `Y` calibrate) and embedded JSON localization (`English` & `Türkçe`) with instant live switching.

---

## 🧪 Tested Platforms, Emulators & Games Compatibility

### Tested Emulators & Games

| Emulator | System | Status | Tested Games & Motion Features | Setup Mode |
| :--- | :--- | :---: | :--- | :--- |
| **Dolphin** (`dolphin.exe`) | Wii / GameCube | ✅ **Verified** | *Mario Kart Wii* (Wii Wheel Sideways tilt steering), *Super Mario Galaxy 1 & 2* (IMUIR Pointer + Shake), *Zelda: Skyward Sword* (MotionPlus) | **Automatic** (Selecting `dolphin.exe` writes `DSUClient.ini`, `WiimoteNew.ini` & `GCPadNew.ini`) |
| **Cemu** (`cemu.exe`) | Wii U | ✅ **Verified** | *The Legend of Zelda: Breath of the Wild* (Bow gyro aiming & apparatus shrines), *Splatoon*, *Mario Kart 8* | `DSUController` (`127.0.0.1:26760`) |
| **Eden / Yuzu / Sudachi / Citron** | Nintendo Switch | ✅ **Verified** | *Zelda: Tears of the Kingdom*, *Breath of the Wild*, *Super Mario Odyssey*, *Splatoon 3*, 8-Player Local Multiplayer | `CemuhookUDP` (`127.0.0.1:26760` + `26762` for Pads 5–8) |
| **Ryujinx** (`ryujinx.exe`) | Nintendo Switch | ✅ **Verified** | *Zelda: Tears of the Kingdom*, *Mario Kart 8 Deluxe*, *Metroid Prime Remastered* | Native CemuHook Motion (`127.0.0.1:26760`, Slot `0..3`) |
| **Azahar / Lime3DS / Citra** | Nintendo 3DS | ✅ **Verified** | *Zelda: Ocarina of Time 3D*, *Majora's Mask 3D*, *Mario Kart 7* | CemuhookUDP Motion Backend (`127.0.0.1:26760`) |
| **RPCS3** (`rpcs3.exe`) | PlayStation 3 | ✅ **Compatible** | SIXAXIS / DualShock 3 tilt & gyro motion games (*Heavy Rain*, *Flower*, *Ratchet & Clank*) | DSU Pad Handler (`127.0.0.1:26760`) |

### Supported Hardware & Operating Systems

| Category | Supported Devices | Motion & Input Source |
| :--- | :--- | :--- |
| **Operating Systems** | **Windows 11 (`22H2` / `23H2` / `24H2` x64)**, **Windows 10 (`19041+` x64)** | WinRT `Windows.Devices.Sensors`, `Windows.Gaming.Input`, `XInput 1.4` |
| **Windows Handhelds** | **ASUS ROG Ally / ROG Ally X**, **Lenovo Legion Go / Go S**, **MSI Claw**, **Ayaneo**, **GPD Win 4 / Mini / Max 2**, **OneXPlayer** | Internal 6-Axis IMU (`Gyrometer` + `Accelerometer`) + Built-in XInput Gamepad |
| **PlayStation Controllers** | **Sony DualSense (`054C:0CE6`)**, **DualSense Edge (`054C:0DF2`)**, **DualShock 4 v1/v2 (`054C:05C4`, `054C:09CC`)** | Auto-Detected via `VID:PID` + `DualSense / DS4` Preset |
| **Nintendo Controllers** | **Switch Pro Controller (`057E:2009`)**, **Joy-Con L/R (`057E:2006`, `057E:2007`)** | Auto-Detected via `VID:PID` + `Switch Pro` Preset |
| **Third-Party Motion Pads** | **8BitDo Ultimate / Pro 2 (`2DC8:*`)**, **GameSir (`3537:*`)**, **Flydigi Vader / Apex** | Auto-Detected via `Windows.Gaming.Input.RawGameController` |
| **Standard XInput Pads** | **Xbox Wireless Controller (`045E:*`)** and XInput-compatible gamepads | Full Button/Stick Forwarding + Paired Handheld IMU / Virtual Routing |

---

## 🚀 Quick Start & Emulator Setup

### 1. Run `HandheldGyroDSU.exe`
Download and launch [**`HandheldGyroDSU.exe`**](https://github.com/brktlhylmz/HandheldGyroDSU/releases/latest/download/HandheldGyroDSU.exe). The server immediately starts broadcasting Cemuhook DSU `v1001` packets on **`127.0.0.1:26760`**.

### 2. Connect Your Emulator

#### **Dolphin (Wii / GameCube) — 1-Click Auto-Setup**
1. In **HandheldGyroDSU**, open the **Controllers** tab → click **Select Emulator `.exe`** under **Emulator Auto-Switch** and pick your `Dolphin.exe`.
2. HandheldGyroDSU automatically enables Dolphin's DSU Client (`127.0.0.1:26760`) and generates ready-to-use controller profiles:
   - **Standard Wii Remote + IR Pointer**: `HandheldGyroDSU_Wiimote` (applied automatically)
   - **Mario Kart Wii / Sideways Wii Wheel**: Load `HandheldGyroDSU_Sideways` in Dolphin's Emulated Wii Remote profile dropdown.

#### **Cemu (Wii U)**
1. Open **Options → Input settings**.
2. Set **Emulated controller** to `Wii U GamePad` (or `Wii U Pro Controller`).
3. Click **`+` (Add)** under **Controller** → select **DSUController** (`127.0.0.1`, port `26760`) and check **Use motion**.

#### **Eden / Yuzu / Sudachi (Nintendo Switch)**
1. Open **Emulation → Configure → Controls**.
2. Click **Configure** next to **Motion** → enable **CemuhookUDP** (`127.0.0.1`, port `26760`) and click **Test**.
3. Click the **Motion** button on the controller view and shake your device to bind `[cemuhookudp]`.

#### **Ryujinx (Nintendo Switch)**
1. Open **Options → Settings → Input → Player 1 Configure**.
2. Enable **Enable Motion Controls** and **Use CemuHook compatible motion** (`127.0.0.1:26760`, Slot `0`).

---

## 🏗️ Architecture Overview

```mermaid
flowchart LR
    subgraph Hardware ["Hardware & Sensors"]
        IMU["WinRT 6-Axis IMU\n(Gyrometer + Accelerometer)"]
        WGI["Windows.Gaming.Input\n& XInput 1.4"]
    end

    subgraph Engine ["HandheldDsuServer (Core 250 Hz Engine)"]
        SS["SensorService (250 Hz)\nTabletop Guard + Pitch Offset + Deadzone"]
        UDP1["Primary DsuUdpServer\n127.0.0.1:26760 (Slots 0..3)"]
        UDP2["Secondary DsuUdpServer\n127.0.0.1:26762 (Slots 4..7)"]
        IPC["IpcHttpServer\n127.0.0.1:26761 (Local JSON API)"]
    end

    subgraph UI ["HandheldGyroDSU (Single-File WPF Overlay)"]
        MW["MainWindow Overlay UI\n(Dashboard / Controllers / Profiles / Settings)"]
        CW["ControllerAndAppWatcherService\nAuto-Slot + Hotkeys + Dolphin Auto-Config"]
        GB["GameBarWidgetAdapter\n(Win + G Companion Hook)"]
        HUD["HudToastService\n(Click-Through In-Game OSD)"]
    end

    IMU --> SS
    WGI --> CW
    WGI --> UDP1
    SS --> UDP1
    SS --> UDP2
    IPC <--> MW
    CW <--> MW
    GB --> MW
    MW --> HUD
```

### Repository Structure

```text
HandheldGyroDSU/
├── HandheldGyroDSU.sln                 # .NET 10 Solution (Core Server + Single-File WPF Overlay)
├── HandheldDsuServer.csproj            # Core 250 Hz DSU UDP Server & CLI Self-Test Engine
├── Program.cs                          # Headless CLI EntryPoint & Automated --self-test Suite
├── Models/                             # AppSettings, AutomationConfig, DeviceProfile, HandheldProfile, MotionData
├── Protocol/                           # Cemuhook DSU v1001 Packet Builder & IEEE 802.3 CRC32
├── Services/                           # SensorService, DsuUdpServer, IpcHttpServer, ProfileManager, StartupManager
├── HandheldDsuWidget/                  # WPF Overlay & Xbox Game Bar Companion (HandheldGyroDSU.exe)
│   ├── MainWindow.xaml                 # Responsive Card Layout & Left Navigation Rail
│   ├── MainWindow.*.cs                 # Modular Partial Classes (Dashboard, Controllers, Profiles, Settings, Tray)
│   ├── Services/                       # ControllerAndAppWatcherService, DsuIpcClient, LocalizationService, HudToastService
│   └── Locales/                        # Embedded & Extensible JSON Dictionaries (en.json, tr.json)
└── .github/workflows/                  # Automated Build, DSU Protocol Self-Test & Single-File .EXE Release CI
```

---

## 🛠️ Building from Source

### Prerequisites
- **Windows 10 (Build 19041+)** or **Windows 11 (x64)**
- **[.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**

### Build, Self-Test & Publish Standalone `.exe`
```powershell
# 1. Clone the repository
git clone https://github.com/brktlhylmz/HandheldGyroDSU.git
cd HandheldGyroDSU

# 2. Build the solution (0 Warnings, 0 Errors)
dotnet build .\HandheldGyroDSU.sln -c Release

# 3. Run the automated Cemuhook DSU v1001 & IPC self-test suite
dotnet run --project .\HandheldDsuServer.csproj -c Release -- --self-test

# 4. Publish a standalone single-file HandheldGyroDSU.exe
dotnet publish .\HandheldDsuWidget\HandheldDsuWidget.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\publish
```

---

## 🤝 Contributing

Contributions, controller `VID:PID` additions, and new language translations (`HandheldDsuWidget/Locales/*.json`) are welcome! Please read [CONTRIBUTING.md](CONTRIBUTING.md) and [AGENTS.md](AGENTS.md) before submitting a pull request.

## 📄 License

Distributed under the **MIT License**. See [LICENSE](LICENSE) for full details.
