#!/usr/bin/env bash
# Wireless build, interactive launch, log streaming, and screenshot utility for ASUS ROG Ally.

set -euo pipefail

SSH_HOST="${ALLY_SSH_HOST:-rog-ally}"
REMOTE_DIR='C:\Users\burak\OneDrive\Belgeler\antigravity-projects'
REMOTE_EXE="$REMOTE_DIR\\HandheldDsuWidget\\bin\\Debug\\net10.0-windows10.0.19041.0\\HandheldGyroDSU.exe"
REMOTE_LOG="$REMOTE_DIR\\ally-debug.log"
LOCAL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

run_remote_ps() {
    local script
    script="\$ProgressPreference = 'SilentlyContinue'; $(cat)"
    local encoded
    encoded="$(printf '%s' "$script" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\r\n')"
    ssh -n "$SSH_HOST" "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded" 2>/dev/null
}

usage() {
    cat <<EOF
Usage: ./scripts/ally-remote.sh <command>

Commands:
  status       Check ROG Ally connection, running process, and .NET SDK version
  sync         Sync local source files from Mac to ROG Ally
  build        Stop running instance, sync files, and run 'dotnet build' on ROG Ally
  run          Sync, build, and launch HandheldGyroDSU interactively on ROG Ally's screen
  restart      Stop running instance and relaunch existing build on ROG Ally's screen
  stop         Stop running HandheldGyroDSU process on ROG Ally
  logs         Show the application log output from ROG Ally
  screenshot   Capture ROG Ally's desktop screen and save to ./ally-screen.png
EOF
}

cmd_status() {
    echo ">>> Checking ROG Ally ($SSH_HOST) status..."
    run_remote_ps <<'EOF'
$sdk = dotnet --version
Write-Output "Dotnet SDK      : $sdk"
$proc = Get-Process HandheldGyroDSU -ErrorAction SilentlyContinue
if ($proc) {
    Write-Output "Running Process : PID=$($proc.Id) | Session=$($proc.SessionId) | Memory=$([math]::Round($proc.WorkingSet64 / 1MB, 1)) MB"
    Write-Output "Executable Path : $($proc.Path)"
} else {
    Write-Output "Running Process : None"
}
EOF
}

cmd_stop() {
    echo ">>> Stopping HandheldGyroDSU on ROG Ally..."
    run_remote_ps <<'EOF'
$proc = Get-Process HandheldGyroDSU -ErrorAction SilentlyContinue
if ($proc) {
    $proc | Stop-Process -Force
    Write-Output "Stopped HandheldGyroDSU (PID: $($proc.Id -join ', '))."
} else {
    Write-Output "HandheldGyroDSU is not running."
}
EOF
}

cmd_sync() {
    echo ">>> Syncing source files from Mac to ROG Ally ($REMOTE_DIR)..."
    COPYFILE_DISABLE=1 tar \
        --exclude='.git' \
        --exclude='.vs' \
        --exclude='bin' \
        --exclude='obj' \
        --exclude='publish' \
        --exclude='ally-screen.png' \
        --exclude='._*' \
        -C "$LOCAL_DIR" -cf - . | ssh "$SSH_HOST" 'tar -xf - -C "C:/Users/burak/OneDrive/Belgeler/antigravity-projects"'
    run_remote_ps <<'EOF'
Get-ChildItem -Path 'C:\Users\burak\OneDrive\Belgeler\antigravity-projects' -File -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name.StartsWith('._') } | Remove-Item -Force -ErrorAction SilentlyContinue
exit 0
EOF
    echo ">>> Sync completed."
}

cmd_build() {
    cmd_stop
    cmd_sync
    echo ">>> Building HandheldGyroDSU.sln on ROG Ally..."
    run_remote_ps <<'EOF'
Set-Location 'C:\Users\burak\OneDrive\Belgeler\antigravity-projects'
dotnet build HandheldGyroDSU.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
EOF
}

cmd_launch_interactive() {
    echo ">>> Launching HandheldGyroDSU on ROG Ally interactive desktop (Session 1)..."
    run_remote_ps <<'EOF'
$remoteDir = 'C:\Users\burak\OneDrive\Belgeler\antigravity-projects'
$taskName = 'HandheldGyroDSU_WirelessDebug'
$exePath = "$remoteDir\HandheldDsuWidget\bin\Debug\net10.0-windows10.0.19041.0\HandheldGyroDSU.exe"
$workDir = "$remoteDir\HandheldDsuWidget\bin\Debug\net10.0-windows10.0.19041.0"
$logPath = "$remoteDir\ally-debug.log"
if (Test-Path $logPath) { Remove-Item $logPath -Force -ErrorAction SilentlyContinue }

$launcherScript = "$remoteDir\launch-debug.cmd"
"@echo off`r`ncd /d `"$workDir`"`r`n`"$exePath`" > `"$logPath`" 2>&1`r`n" | Set-Content -Path $launcherScript -Encoding ASCII

$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$launcherScript`"" -WorkingDirectory $workDir
$principal = New-ScheduledTaskPrincipal -UserId 'burak' -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 0)

Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Start-Sleep -Milliseconds 1500

$proc = Get-Process HandheldGyroDSU -ErrorAction SilentlyContinue
if ($proc) {
    Write-Output "Successfully launched HandheldGyroDSU on ROG Ally! PID=$((@($proc)[0]).Id) Session=$((@($proc)[0]).SessionId)"
} else {
    Write-Output "Warning: Process did not stay running. Log output:"
    if (Test-Path $logPath) { Get-Content $logPath }
}
EOF
}

cmd_run() {
    cmd_build
    cmd_launch_interactive
}

cmd_restart() {
    cmd_stop
    cmd_launch_interactive
}

cmd_logs() {
    echo ">>> Reading logs from ROG Ally ($REMOTE_LOG)..."
    run_remote_ps <<'EOF'
$logPath = 'C:\Users\burak\OneDrive\Belgeler\antigravity-projects\ally-debug.log'
if (Test-Path $logPath) {
    Get-Content $logPath -Tail 100
} else {
    Write-Output "No log file found at $logPath yet."
}
EOF
}

cmd_screenshot() {
    local out_file="${1:-$LOCAL_DIR/ally-screen.png}"
    echo ">>> Capturing interactive desktop screenshot from ROG Ally..."
    run_remote_ps <<'EOF'
$remoteDir = 'C:\Users\burak\OneDrive\Belgeler\antigravity-projects'
$remotePng = "$remoteDir\ally-screen.png"
$captureScript = "$remoteDir\capture-screen.ps1"
if (Test-Path $remotePng) { Remove-Item $remotePng -Force -ErrorAction SilentlyContinue }

@'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class DpiHelper {
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
}
"@
[DpiHelper]::SetProcessDPIAware() | Out-Null
$screens = [System.Windows.Forms.Screen]::AllScreens
$bounds = $screens[0].Bounds
$bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bmp.Save("C:\Users\burak\OneDrive\Belgeler\antigravity-projects\ally-screen.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
'@ | Set-Content -Path $captureScript -Encoding UTF8

$taskName = 'HandheldGyroDSU_Screenshot'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$captureScript`""
$principal = New-ScheduledTaskPrincipal -UserId 'burak' -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName

for ($i = 0; $i -lt 30; $i++) {
    if (Test-Path $remotePng) { break }
    Start-Sleep -Milliseconds 200
}
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item $captureScript -Force -ErrorAction SilentlyContinue
EOF
    scp -q "$SSH_HOST:C:/Users/burak/OneDrive/Belgeler/antigravity-projects/ally-screen.png" "$out_file"
    echo ">>> Screenshot saved to: $out_file"
}

COMMAND="${1:-status}"
case "$COMMAND" in
    status)     cmd_status ;;
    sync)       cmd_sync ;;
    build)      cmd_build ;;
    run)        cmd_run ;;
    restart)    cmd_restart ;;
    stop)       cmd_stop ;;
    logs)       cmd_logs ;;
    screenshot) cmd_screenshot "${2:-}" ;;
    *)          usage; exit 1 ;;
esac
