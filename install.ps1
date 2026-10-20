#Requires -Version 5.1
<#
.SYNOPSIS
    Handheld Gyro DSU - Xbox Game Bar Widget Sideloading & Kurulum Betiği (ROG Ally / Legion Go / MSI Claw)
.DESCRIPTION
    Microsoft Store gerekmeden yerel Geliştirici Modunda (Developer Mode):
    1. Windows Geliştirici Modunu ve Sideloading izinlerini doğrular/etkinleştirir.
    2. Self-Signed (CN=HandheldDsuDev) sertifikası oluşturur ve TrustedPeople / Root deposuna yükler.
    3. Çözümü (HandheldDsuServer + HandheldDsuWidget) derler ve gerekli manifest logolarını oluşturur.
    4. Add-AppxPackage -Register ile Xbox Game Bar Widget uzantısını (windows.xboxGameBarWidget) sisteme kaydeder.
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
Write-Host "  HANDHELD GYRO DSU - XBOX GAME BAR WIDGET KURULUM ARACI (SIDELOAD)  " -ForegroundColor Cyan
Write-Host "=====================================================================" -ForegroundColor Cyan

# 1. Yönetici Yetkisi Kontrolü
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "[!] UYARI: Sertifika kurulumu ve Geliştirici Modu kaydı için PowerShell'i 'Yönetici Olarak' çalıştırmanız önerilir." -ForegroundColor Yellow
}

# 2. Windows Geliştirici Modu (Developer Mode & Sideloading) Etkinleştirme
if ($isAdmin) {
    Write-Host "[1/5] Windows Geliştirici Modu (Sideloading) kayıt defteri anahtarları kontrol ediliyor..." -ForegroundColor Green
    $unlockPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"
    if (-not (Test-Path $unlockPath)) {
        New-Item -Path $unlockPath -Force | Out-Null
    }
    Set-ItemProperty -Path $unlockPath -Name "AllowDevelopmentWithoutDevLicense" -Value 1 -Type DWord -Force
    Set-ItemProperty -Path $unlockPath -Name "AllowAllTrustedApps" -Value 1 -Type DWord -Force
    Write-Host "      -> Geliştirici Modu ve Sideloading aktif." -ForegroundColor Gray
} else {
    Write-Host "[1/5] Yönetici yetkisi olmadığı için Geliştirici Modu kayıt defteri adımı atlandı (Windows Ayarları > Geliştiriciler için > Geliştirici Modu açık olmalıdır)." -ForegroundColor Yellow
}

# 3. Self-Signed Sertifika Oluşturma ve Güvenilen Depolara Kurulum
if (-not $SkipCert) {
    Write-Host "[2/5] Self-Signed Geliştirici Sertifikası ($PublisherSubject) kontrol ediliyor..." -ForegroundColor Green
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
        Write-Host "      -> Yeni sertifika oluşturuldu: $($existingCert.Thumbprint)" -ForegroundColor Gray
    } else {
        Write-Host "      -> Mevcut sertifika bulundu: $($existingCert.Thumbprint)" -ForegroundColor Gray
    }

    Export-Certificate -Cert $existingCert -FilePath $CertPath -Force | Out-Null

    if ($isAdmin) {
        Import-Certificate -FilePath $CertPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
        Import-Certificate -FilePath $CertPath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
        Write-Host "      -> Sertifika LocalMachine\TrustedPeople ve Root depolarına eklendi." -ForegroundColor Gray
    } else {
        Import-Certificate -FilePath $CertPath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
        Write-Host "      -> Sertifika CurrentUser\TrustedPeople deposuna eklendi." -ForegroundColor Gray
    }
}

# 4. Manifest Görsellerini (Assets) ve Çözümü Derleme
Write-Host "[3/5] Widget manifest logoları (Assets) hazırlanıyor..." -ForegroundColor Green
$AssetsDir = Join-Path $WidgetProjectDir "Assets"
if (-not (Test-Path $AssetsDir)) {
    New-Item -ItemType Directory -Path $AssetsDir -Force | Out-Null
}

# Geçerli 1x1 PNG şablonu (AppxManifest doğrulaması için zorunludur)
$minimalPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAACwAAAAsCAYAAAAehFoBAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAtargetSURBVFhH7c4xAQAAAMKg9U9tCy+gAAAAAAAAAAAA4G8BGrAAAb82s6kAAAAASUVORK5CYII="
$pngBytes = [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkWPjfDwAEgQH59C9X/wAAAABJRU5ErkJggg==")
foreach ($logoName in @("StoreLogo.png", "Square150x150Logo.png", "Square44x44Logo.png")) {
    $logoFile = Join-Path $AssetsDir $logoName
    if (-not (Test-Path $logoFile)) {
        [IO.File]::WriteAllBytes($logoFile, $pngBytes)
    }
}

Write-Host "[4/5] Çözüm (.NET 10) derleniyor..." -ForegroundColor Green
Push-Location $ProjectRoot
try {
    dotnet build HandheldDsu.sln -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build başarısız oldu!"
    }
} finally {
    Pop-Location
}

# 5. Add-AppxPackage -Register ile Xbox Game Bar Widget Kaydı
$SourceManifestPath = Join-Path $WidgetOutDir "Package.appxmanifest"
$ManifestPath = Join-Path $WidgetOutDir "AppxManifest.xml"
Copy-Item -Path $SourceManifestPath -Destination $ManifestPath -Force
Write-Host "[5/5] Xbox Game Bar Widget sisteme kaydediliyor ($ManifestPath)..." -ForegroundColor Green

try {
    Add-AppxPackage -Register $ManifestPath -ForceApplicationShutdown -ForceUpdateFromAnyVersion
    $pkg = Get-AppxPackage -Name "HandheldDsuWidget" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pkg) {
        CheckNetIsolation.exe LoopbackExempt -a -n="$($pkg.PackageFamilyName)" | Out-Null
    }
    Write-Host ""
    Write-Host "=====================================================================" -ForegroundColor Green
    Write-Host " [OK] KURULUM TAMAMLANDI!" -ForegroundColor Green
    Write-Host "  1. DSU Sunucusunu başlatmak için : dotnet run --project HandheldDsuServer.csproj" -ForegroundColor White
    Write-Host "  2. Xbox Game Bar'ı açmak için    : Win + G (Widget Menüsü > Handheld Gyro DSU)" -ForegroundColor White
    Write-Host "  3. Masaüstü Overlay olarak açmak : .\HandheldDsuWidget\bin\$Configuration\net10.0-windows10.0.19041.0\HandheldDsuWidget.exe" -ForegroundColor White
    Write-Host "=====================================================================" -ForegroundColor Green
} catch {
    Write-Host ""
    Write-Host "[!] Add-AppxPackage uyarısı: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "    Not: Windows Ayarları > Sistem > Geliştiriciler İçin > 'Geliştirici Modu' seçeneğinin açık olduğundan emin olun." -ForegroundColor Yellow
    Write-Host "    Widget'ı masaüstü overlay modunda doğrudan çalıştırmak için:" -ForegroundColor Cyan
    Write-Host "    .\HandheldDsuWidget\bin\$Configuration\net10.0-windows10.0.19041.0\HandheldDsuWidget.exe" -ForegroundColor White
}
