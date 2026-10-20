#Requires -Version 5.1
<#
.SYNOPSIS
    Handheld Gyro DSU - Xbox Game Bar Widget Sideloading & Setup Script
.DESCRIPTION
    Configures and registers the Xbox Game Bar Widget locally in Windows Developer Mode:
    1. Verifies/enables Windows Developer Mode and Sideloading registry keys.
    2. Creates a Self-Signed certificate (CN=HandheldDsuDev) and installs it into TrustedPeople / Root stores.
    3. Builds the solution (HandheldDsuServer + HandheldDsuWidget) and prepares manifest assets.
    4. Registers the Xbox Game Bar Widget extension via Add-AppxPackage -Register.
#>

param(
    [string]$Configuration = "Debug",
    [switch]$SkipCert
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$WidgetProjectDir = Join-Path $ProjectRoot "HandheldDsuWidget"
$WidgetOutDir = Join-Path $WidgetProjectDir "bin\$Configuration\net10.0-windows10.0.19041.0"
$PublisherSubject = "CN=HandheldDsuDev"
$CertPath = Join-Path $ProjectRoot "HandheldDsuDev.cer"

Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host "  HANDHELD GYRO DSU - XBOX GAME BAR WIDGET SETUP TOOL (SIDELOAD)     " -ForegroundColor Cyan
Write-Host "=====================================================================" -ForegroundColor Cyan

# 1. Administrator Privilege Check
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "[!] WARNING: Running PowerShell as Administrator is recommended for certificate and Developer Mode setup." -ForegroundColor Yellow
}

# 2. Enable Windows Developer Mode & Sideloading
if ($isAdmin) {
    Write-Host "[1/5] Checking Windows Developer Mode (Sideloading) registry keys..." -ForegroundColor Green
    $unlockPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"
    if (-not (Test-Path $unlockPath)) {
        New-Item -Path $unlockPath -Force | Out-Null
    }
    Set-ItemProperty -Path $unlockPath -Name "AllowDevelopmentWithoutDevLicense" -Value 1 -Type DWord -Force
    Set-ItemProperty -Path $unlockPath -Name "AllowAllTrustedApps" -Value 1 -Type DWord -Force
    Write-Host "      -> Developer Mode and Sideloading enabled." -ForegroundColor Gray
} else {
    Write-Host "[1/5] Skipping Developer Mode registry step (non-admin session). Ensure Developer Mode is enabled in Windows Settings." -ForegroundColor Yellow
}

# 3. Create Self-Signed Certificate and Install into Trusted Stores
if (-not $SkipCert) {
    Write-Host "[2/5] Checking Self-Signed Developer Certificate ($PublisherSubject)..." -ForegroundColor Green
    $existingCert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $PublisherSubject } | Select-Object -First 1

    if (-not $existingCert) {
        $existingCert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $PublisherSubject `
            -KeyUsage DigitalSignature `
            -FriendlyName "Handheld Gyro DSU Game Bar Dev Cert" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
            -NotAfter (Get-Date).AddYears(5)
        Write-Host "      -> Created new certificate: $($existingCert.Thumbprint)" -ForegroundColor Gray
    } else {
        Write-Host "      -> Found existing certificate: $($existingCert.Thumbprint)" -ForegroundColor Gray
    }

    Export-Certificate -Cert $existingCert -FilePath $CertPath -Force | Out-Null

    if ($isAdmin) {
        Import-Certificate -FilePath $CertPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
        Import-Certificate -FilePath $CertPath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
        Write-Host "      -> Installed certificate into LocalMachine\TrustedPeople and Root stores." -ForegroundColor Gray
    } else {
        Import-Certificate -FilePath $CertPath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
        Write-Host "      -> Installed certificate into CurrentUser\TrustedPeople store." -ForegroundColor Gray
    }
}

# 4. Prepare Manifest Assets and Build Solution
Write-Host "[3/5] Preparing Widget manifest logo assets..." -ForegroundColor Green
$AssetsDir = Join-Path $WidgetProjectDir "Assets"
if (-not (Test-Path $AssetsDir)) {
    New-Item -ItemType Directory -Path $AssetsDir -Force | Out-Null
}

# Valid minimal PNG template required for AppxManifest validation
$pngBytes = [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkWPjfDwAEgQH59C9X/wAAAABJRU5ErkJggg==")
foreach ($logoName in @("StoreLogo.png", "Square150x150Logo.png", "Square44x44Logo.png")) {
    $logoFile = Join-Path $AssetsDir $logoName
    if (-not (Test-Path $logoFile)) {
        [IO.File]::WriteAllBytes($logoFile, $pngBytes)
    }
}

Write-Host "[4/5] Building solution (.NET 10)..." -ForegroundColor Green
Push-Location $ProjectRoot
try {
    dotnet build HandheldDsu.sln -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed!"
    }
} finally {
    Pop-Location
}

# 5. Register Xbox Game Bar Widget via Add-AppxPackage -Register
$SourceManifestPath = Join-Path $WidgetOutDir "Package.appxmanifest"
$ManifestPath = Join-Path $WidgetOutDir "AppxManifest.xml"
Copy-Item -Path $SourceManifestPath -Destination $ManifestPath -Force
Write-Host "[5/5] Registering Xbox Game Bar Widget ($ManifestPath)..." -ForegroundColor Green

try {
    Add-AppxPackage -Register $ManifestPath -ForceApplicationShutdown -ForceUpdateFromAnyVersion
    $pkg = Get-AppxPackage -Name "HandheldDsuWidget" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pkg) {
        CheckNetIsolation.exe LoopbackExempt -a -n="$($pkg.PackageFamilyName)" | Out-Null
    }
    Write-Host ""
    Write-Host "=====================================================================" -ForegroundColor Green
    Write-Host " [OK] INSTALLATION COMPLETED!" -ForegroundColor Green
    Write-Host "  1. Start DSU Console Server : dotnet run --project HandheldDsuServer.csproj" -ForegroundColor White
    Write-Host "  2. Open Xbox Game Bar       : Win + G (Widget Menu > Handheld Gyro DSU)" -ForegroundColor White
    Write-Host "  3. Launch Desktop Widget    : .\HandheldDsuWidget\bin\$Configuration\net10.0-windows10.0.19041.0\HandheldDsuWidget.exe" -ForegroundColor White
    Write-Host "=====================================================================" -ForegroundColor Green
} catch {
    Write-Host ""
    Write-Host "[!] Add-AppxPackage warning: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "    Note: Ensure 'Developer Mode' is enabled under Windows Settings > System > For developers." -ForegroundColor Yellow
    Write-Host "    You can run the Widget directly in desktop mode via:" -ForegroundColor Cyan
    Write-Host "    .\HandheldDsuWidget\bin\$Configuration\net10.0-windows10.0.19041.0\HandheldDsuWidget.exe" -ForegroundColor White
}
