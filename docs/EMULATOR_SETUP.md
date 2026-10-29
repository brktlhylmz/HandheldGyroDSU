# 🎮 Emulator Setup Guide (Dolphin, RPCS3, Cemu, Switch & PC Games)

**HandheldGyroDSU** streams native 250 Hz 6-axis motion (`Gyrometer` + `Accelerometer`) and full gamepad inputs over the **Cemuhook DSU (`v1001`)** UDP protocol (`127.0.0.1:26760`), while also providing one-click configuration tools for **Dolphin** and **RPCS3** and a per-profile **Gyro-to-Mouse** engine for PC/Steam games.

---

## 📑 Table of Contents
1. [Dolphin Emulator (Wii & GameCube) — One-Click Setup](#-1-dolphin-emulator-wii--gamecube--one-click-setup)
2. [Super Mario Galaxy 1 & 2 — Recommended Controls](#-2-super-mario-galaxy-1--2--recommended-controls)
3. [RPCS3 (PlayStation 3) — Handheld SDL Gyro & Axis Patcher](#-3-rpcs3-playstation-3--handheld-sdl-gyro--axis-patcher)
4. [Cemu (Wii U)](#-4-cemu-wii-u)
5. [Eden / Yuzu / Sudachi / Citron (Nintendo Switch)](#-5-eden--yuzu--sudachi--citron-nintendo-switch)
6. [Ryujinx (Nintendo Switch)](#-6-ryujinx-nintendo-switch)
7. [PC & Steam Games (Per-Profile Gyro-to-Mouse)](#-7-pc--steam-games-per-profile-gyro-to-mouse)

---

## 🐬 1. Dolphin Emulator (Wii & GameCube) — One-Click Setup

HandheldGyroDSU includes a built-in **Dolphin DSU & Motion Profile Installer** that configures Dolphin's DSU Client (`DSUClient.ini`), enables background input (`Dolphin.ini`), and installs ready-to-play **Wii Remote** and **GameCube** profiles bound directly to `DSUClient/0/HandheldGyroDSU`.

![Dolphin & RPCS3 Configurator Tools](screenshots/emulator-tools.png)

### How to Configure Dolphin
1. Open **HandheldGyroDSU** and go to the **Settings** tab.
2. Scroll down to **Dolphin DSU & Motion Profile Installer**.
3. If your Dolphin installation (Standard `%AppData%\Dolphin Emulator\Config`, Documents, or **EmuDeck**) is detected automatically, click **`CONFIGURE DOLPHIN DSU & PROFILES`**.
4. If you use a portable Dolphin folder in a custom location, click **`BROWSE...`**, select your `Dolphin.exe`, and click **`CONFIGURE DOLPHIN DSU & PROFILES`**.
5. **Full Controller Forwarding Prompt**:
   - Because the installed Dolphin profiles bind **`DSUClient/0/HandheldGyroDSU`** for both buttons/sticks and 6-axis motion, HandheldGyroDSU checks whether **Forward Full Controller (Buttons + Sticks + Gyro)** is enabled on the Dashboard.
   - If it is currently turned OFF, HandheldGyroDSU will ask if you want to enable it automatically (**Yes** = enable & install, **No** = install without changing setting, **Cancel** = abort).

### What the Installer Configures Automatically
- **`DSUClient.ini`**:
  ```ini
  [Server]
  Enabled = True
  Entries = HandheldGyroDSU:127.0.0.1:26760;
  ```
- **Device & Button Mapping (`WiimoteNew.ini` & `GCPadNew.ini`)**:
  - Primary device is set to **`DSUClient/0/HandheldGyroDSU`**.
  - Buttons use native Cemuhook DSU PlayStation identifiers (`Cross`, `Circle`, `Square`, `Triangle`, `L1`, `R1`, `L2`, `R2`, `Share`, `Options`, `PS`, `L3`, `R3`) with fallback bindings for `XInput/0/Gamepad` so controls work seamlessly in every mode.
- **Installed Dolphin Profiles (`Config/Profiles/Wiimote/` & `GCPad/`)**:
  - **`HandheldGyroDSU_Xbox`** *(Default active in `WiimoteNew.ini`)*: Wii Remote + Nunchuk + `IMUIR` Gyro Pointer + 6-Axis Motion + Shake (`L1`).
  - **`HandheldGyroDSU_Sideways`**: Horizontal Wii Remote (**Mario Kart Wii** / *New Super Mario Bros. Wii*) with `Sideways Toggle = True` and upright/sideways tilt steering math.
  - **`HandheldGyroDSU_Xbox` (GameCube Pad)**: Full GameCube controller layout (`GCPadNew.ini`).

### Default Hotkeys in Dolphin Wii Remote Profile
| Action | Handheld Button | DSU Identifier | Purpose |
| :--- | :---: | :---: | :--- |
| **Recenter IR Pointer & Motion** | **Right Stick Click (`R3`)** | `R3` | Instantly centers the Wii Remote pointer (`IMUIR/Recenter`) and resets gyro posture |
| **Toggle Upright Wii Remote** | **Left Stick Click (`L3`)** | `L3` | Toggles **Upright Wii Remote** orientation (essential for *Super Mario Galaxy* Manta Ray surfing & Rolling Ball stages) |
| **Shake Wii Remote (X/Y/Z)** | **Left Bumper (`LB`)** | `L1` | Performs Wii Remote Spin / Shake attack |

---

## 🌟 2. Super Mario Galaxy 1 & 2 — Recommended Controls

*Super Mario Galaxy* uses two distinct Wii Remote postures depending on the level:

1. **Normal Gameplay (Star Bits Pointer + Spin Attack)**:
   - Hold your handheld naturally and tilt to aim the Star Pointer (`IMUIR`).
   - Press **`R3` (Right Stick Click)** at any time to center the pointer in the middle of the screen.
   - Press **`LB` (`L1`)** to Spin Attack.
   - Collect Star Bits with **`LT` (`L2` -> Wii `B` Trigger)** and jump with **`A` (`Cross` -> Wii `A` Button)**.
2. **Rolling Ball (Star Ball) & Manta Ray Surfing Levels**:
   - In these stages, the original Wii game expects you to hold the Wii Remote **vertically (Upright)** or tilt it like a flight stick.
   - Press **`L3` (Left Stick Click)** once when the mini-game starts to toggle **`Upright Wii Remote`** mode, then press **`R3`** to recenter your neutral holding angle!
   - When the mini-game finishes, press **`L3`** again to return to standard pointer posture.

> [!IMPORTANT]
> **Disable OEM Handheld Gyro-to-Stick / Gyro-to-Mouse While Using Emulators!**
> If you use **ASUS Armoury Crate**, **Lenovo Legion Space**, or **Steam Input**, make sure Gyro is set to **OFF** in your Armoury Crate / Steam game profile for Dolphin. If Armoury Crate converts your gyro into Right Stick or Mouse while HandheldGyroDSU streams native DSU motion, both systems will move the camera/cursor simultaneously.

---

## 🎮 3. RPCS3 (PlayStation 3) — Handheld SDL Gyro & Axis Patcher

### Why RPCS3 Needs Special Handling on Windows Handhelds
Unlike Dolphin, Cemu, or Switch emulators, **RPCS3** does not have a built-in Cemuhook DSU UDP client, and its `XInput` pad handler does not support DualShock 3 **SIXAXIS** motion controls (`Motion Sensor X/Y/Z/G`).

While RPCS3 includes an **`SDL`** pad handler that supports motion sensors via SDL3, handhelds like the **ASUS ROG Ally** and **Lenovo Legion Go** fail to work out of the box for two technical reasons inside `rpcs3.exe`:
1. **Missing `SDL_INIT_SENSOR` on the Main Thread**: RPCS3 calls `SDL_Init(0x00013000)` (`SDL_INIT_HAPTIC | SDL_INIT_GAMEPAD | SDL_INIT_CAMERA`) without `SDL_INIT_SENSOR (0x8000)`. When the SDL pad thread later tries to enable sensors on a background worker thread, Windows `Windows.Devices.Sensors` WinRT events fail to fire or are blocked.
2. **Missing `SDL_INIT_VIDEO` for Portrait-to-Landscape Display Axis Rotation**: Most Windows handhelds use native portrait display panels rotated 90°/270° in Windows to landscape. Without `SDL_INIT_VIDEO (0x20)`, SDL3 cannot query `SDL_GetNaturalDisplayOrientation` vs `SDL_GetCurrentDisplayOrientation`, causing pitch and roll axes to be swapped or inverted in SIXAXIS games.

### How to Apply the One-Click RPCS3 Patch
1. Open **HandheldGyroDSU → Settings → RPCS3 Handheld SDL Gyro & Axis Patcher**.
2. HandheldGyroDSU automatically searches common paths (`%AppData%\emudeck\Emulators\rpcs3\rpcs3.exe`, `%AppData%\rpcs3\rpcs3.exe`, `C:\Emulation\Emulators\rpcs3\rpcs3.exe`, or a running `rpcs3` process). If your RPCS3 is in a custom folder, click **`BROWSE...`** and select `rpcs3.exe`.
3. Click **`APPLY SDL GYRO PATCH`**.

### What the Patcher Does Under the Hood
- **Safe Automatic Backup**: Creates `rpcs3.exe.bak` in the same folder before modifying a single byte. You can click **`RESTORE ORIGINAL`** at any time to revert `rpcs3.exe` to its unmodified state.
- **Binary `SDL_Init` Flag Upgrade (`0x00013000` → `0x0001B020`)**:
  - Scans `rpcs3.exe` for the x64 `mov ecx, 0x00013000; call SDL_Init` pattern (`B9 00 30 01 00 FF 15` / `E8`) and updates the flags to **`0x0001B020`**:
    ```text
    0x0001B020 = SDL_INIT_VIDEO (0x00020)
               | SDL_INIT_HAPTIC (0x01000)
               | SDL_INIT_GAMEPAD (0x02000)
               | SDL_INIT_SENSOR (0x08000)
               | SDL_INIT_CAMERA (0x10000)
    ```
- **Automatic `Default.yml` Controller Configuration**:
  - Updates `config/input_configs/global/Default.yml` inside your RPCS3 directory so `Player 1 Input` automatically uses **`Handler: SDL`** and **`Device: XInput Controller 1`** (instead of `Handler: XInput`).
- **Zero Driver Conflicts**:
  - Works natively with your handheld's built-in XInput controller and internal Windows IMU—no `ViGEmBus`, `HidHide`, or virtual DualShock 4 emulation required.

> [!NOTE]
> **After Updating RPCS3:** Whenever RPCS3's built-in updater downloads a new `rpcs3.exe`, the badge in **HandheldGyroDSU → Settings** will automatically change from `✓ PATCHED & ACTIVE` to `UNPATCHED`. Simply click **`APPLY SDL GYRO PATCH`** again to re-enable handheld SIXAXIS motion.

---

## 🟦 4. Cemu (Wii U)

1. Open Cemu and go to **Options → Input settings**.
2. Set **Emulated controller** to **`Wii U GamePad`**.
3. Click **`+` (Add)** under **Controller**:
   - **API**: `DSUController`
   - **IP**: `127.0.0.1`
   - **Port**: `26760`
   - **Controller**: `Controller 1` (`Slot 0`)
4. Check **`Use motion`** in Settings.

---

## 🟥 5. Eden / Yuzu / Sudachi / Citron (Nintendo Switch)

1. Open **Emulation → Configure → Controls**.
2. Ensure **Motion** is checked at the bottom.
3. Click **Configure** next to **Motion**:
   - Confirm **`127.0.0.1`** and port **`26760`** are listed under **CemuhookUDP** servers and click **Test**.
4. Click the **`Motion 1`** button on the controller diagram and gently shake your handheld until it binds to `cemuhookudp`.

---

## 🟧 6. Ryujinx (Nintendo Switch)

1. Open **Options → Settings → Input → Player 1 Configure**.
2. Select your handheld controller under **Input Device**.
3. Check **Enable Motion Controls** and **Use CemuHook compatible motion**:
   - **Server Host**: `127.0.0.1 : 26760`
   - **Controller Slot**: `0`

---

## 🖱️ 7. PC & Steam Games (Per-Profile Gyro-to-Mouse)

For native PC games that don't support Cemuhook DSU:
1. Open **HandheldGyroDSU → Profiles**.
2. Select or create a profile for your game, and enable **Gyro-to-Mouse (PC / Steam Games)**.
3. Choose an **Activation Mode**:
   - **Hold Button (ADS)**: Only moves the mouse while holding a controller button (default: **Left Trigger `LT`**, customizable via **`Change Button`**).
   - **Toggle Button**: Press the bound button once to toggle gyro aiming ON/OFF.
   - **Always On**: Translates gyro into mouse movement continuously while in-game.
4. *(Note)*: Gyro-to-Mouse automatically pauses whenever the **HandheldGyroDSU** window is focused so you can always click UI controls effortlessly.
