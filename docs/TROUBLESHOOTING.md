# 🛠️ Troubleshooting & FAQ

This guide covers common questions and issues when using **HandheldGyroDSU** on Windows handhelds (**ASUS ROG Ally / Ally X**, **Lenovo Legion Go**, **MSI Claw**, **Ayaneo**, **GPD**).

---

## 1. "Movements feel inverted, doubled, or fight with my right stick in emulators"

### Cause: OEM Software (Armoury Crate / Legion Space / Steam Input) Gyro Mapping Conflict
On the **ASUS ROG Ally**, if **Armoury Crate SE** has **Gyro** set to *"As Mouse"* or *"As Right Stick"* in your Game Profile (or Desktop Mode), tilting the handheld sends **both**:
1. Fake right-stick / mouse movements from Armoury Crate, **AND**
2. Real 6-axis Cemuhook DSU motion from **HandheldGyroDSU**.

### Fix
1. Open **Armoury Crate SE** (or **Command Center**) and set **Gyro Behavior** to **OFF** for your emulator (`Dolphin.exe`, `Cemu.exe`, `rpcs3.exe`, `eden.exe`, etc.), and make sure **Control Mode** is set to **Gamepad Mode** (not Desktop Mode).
2. If you launch your emulator through **Steam**, disable **Steam Input Gyro** for that shortcut.
3. In **HandheldGyroDSU → Dashboard**, click **`RESET DEFAULTS`** under **Profile & Axis Orientation** so the verified **`Standard Landscape`** axis mapping (`Invert Pitch: OFF`, `Invert Roll: ON`, `Invert Yaw: ON`) is restored.

---

## 2. "In *Super Mario Galaxy* (Dolphin), the Star Ball / Manta Ray Surfing tilts the wrong way"

### Cause: Horizontal Pointer Posture vs. Vertical (`Upright`) Wii Remote Posture
In *Super Mario Galaxy*, normal platforming expects the Wii Remote to point horizontally at the screen (`IMUIR` Pointer), whereas **Rolling Ball (Star Ball)** and **Manta Ray Surfing** stages require holding the physical Wii Remote **vertically (`Upright` like a flight stick)**.

### Fix
1. Open **HandheldGyroDSU → Settings → Dolphin DSU & Motion Profile Installer** and click **`CONFIGURE DOLPHIN DSU & PROFILES`**.
2. During normal gameplay, press **`R3` (Right Stick Click)** to center the Star Pointer (`IMUIR/Recenter`).
3. When entering a **Rolling Ball** or **Manta Ray Surfing** stage:
   - Press **`L3` (Left Stick Click)** once to toggle **`Upright Wii Remote`** mode.
   - Hold your handheld at a comfortable viewing angle and press **`R3` (Right Stick Click)** to recenter neutral posture.
   - When the mini-game ends, press **`L3`** again to return to normal pointer mode.

---

## 3. "Dolphin ignores DSU motion or EmuDeck overwrote my Dolphin profile"

### Cause
- Dolphin requires `DSUClient.ini` to use the **`[Server]`** section header (`Entries = HandheldGyroDSU:127.0.0.1:26760;`).
- **EmuDeck** pre-configures `WiimoteNew.ini` with `DSUClient/0/steamdeckgyro`, which won't match if the DSU server has a different name or if Dolphin is located in a custom folder (`%AppData%\emudeck\Emulators\Dolphin-x64\User\Config`).

### Fix
1. Open **HandheldGyroDSU → Settings → Dolphin DSU & Motion Profile Installer**.
2. Verify the detected Dolphin config path (or click **`BROWSE...`** and select your `Dolphin.exe`).
3. Click **`CONFIGURE DOLPHIN DSU & PROFILES`**.
4. If **Forward Full Controller (Buttons + Sticks + Gyro)** is turned OFF, HandheldGyroDSU will ask if you want to enable it—click **Yes** so `DSUClient/0/HandheldGyroDSU` receives both buttons/sticks and 6-axis motion.

---

## 4. "RPCS3 SIXAXIS motion stopped working after updating RPCS3 (or axes are swapped)"

### Cause
- RPCS3's built-in updater replaces `rpcs3.exe` with a fresh binary that calls `SDL_Init(0x00013000)` without `SDL_INIT_SENSOR (0x8000)` or `SDL_INIT_VIDEO (0x20)`.
- Or a custom per-game input configuration in RPCS3 is still set to `Handler: XInput` instead of `Handler: SDL`.

### Fix
1. Open **HandheldGyroDSU → Settings → RPCS3 Handheld SDL Gyro & Axis Patcher**.
2. Notice the status badge says **`UNPATCHED`** after an RPCS3 update. Click **`APPLY SDL GYRO PATCH`** to upgrade `SDL_Init` to `0x0001B020` (`SDL_INIT_VIDEO | SDL_INIT_HAPTIC | SDL_INIT_GAMEPAD | SDL_INIT_SENSOR | SDL_INIT_CAMERA`) and configure `Default.yml`.
3. In RPCS3, open **Pads** (or right-click your game → **Change Custom Input Configuration**) and verify that **Handlers** is set to **`SDL`** and **Devices** is set to **`XInput Controller 1`**.
4. Need to revert `rpcs3.exe`? Click **`RESTORE ORIGINAL`** in **HandheldGyroDSU → Settings** to restore `rpcs3.exe.bak`.

---

## 5. "Calibrate Gyro button says 'Place on a Flat Surface' or Resting Angle feels tilted"

### Cause: Tabletop Calibration Guard vs. In-Hand Recenter
- The bottom **`CALIBRATE GYRO & RESTING ANGLE`** button performs a hardware zero-rate bias calibration. When **Tabletop (Stationary) Calibration Guard** is enabled in **Settings**, it checks that the device is stationary (`±1.5°/s`) and flat (`AccZ ≤ -0.82G`).
- If you calibrate while holding the handheld at a steep sideways roll angle, wait until you hold the screen level left-to-right, or use the **In-Game Quick Recenter Hotkey** (`Controllers` tab → `Bind Hotkey`, e.g., `Select + R3`).
