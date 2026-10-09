# One-shot build: publishes a self-contained Toasty.exe (no .NET install needed on the target PC),
# fetches and verifies the PawnIO driver installer, renders the wizard images, and compiles
# dist\Toasty-Setup-<version>.exe with Inno Setup.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$version = ([xml](Get-Content "$root\Toasty.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Building Toasty $version" -ForegroundColor Cyan

# 1. Self-contained single-file exe (bundles the .NET runtime).
if (Test-Path "$root\publish") { Remove-Item "$root\publish" -Recurse -Force }
dotnet publish "$root\Toasty.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 2. PawnIO redistributable, pinned and hash-checked.
$pawnUrl  = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
$pawnHash = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
$pawnPath = "$root\installer\redist\PawnIO_setup.exe"
New-Item -ItemType Directory -Force (Split-Path $pawnPath) | Out-Null
if (-not (Test-Path $pawnPath) -or (Get-FileHash $pawnPath -Algorithm SHA256).Hash -ne $pawnHash) {
    Write-Host "Downloading PawnIO 2.2.0..."
    Invoke-WebRequest $pawnUrl -OutFile $pawnPath
}
if ((Get-FileHash $pawnPath -Algorithm SHA256).Hash -ne $pawnHash) {
    Remove-Item $pawnPath
    throw "PawnIO_setup.exe hash mismatch; refusing to bundle it."
}

# 2b. PresentMon (Intel, MIT) for FPS / frame-time capture, pinned and hash-checked.
$pmUrl  = 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe'
$pmHash = 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
$pmPath = "$root\installer\redist\PresentMon.exe"
if (-not (Test-Path $pmPath) -or (Get-FileHash $pmPath -Algorithm SHA256).Hash -ne $pmHash) {
    Write-Host "Downloading PresentMon 2.6.0..."
    Invoke-WebRequest $pmUrl -OutFile $pmPath
}
if ((Get-FileHash $pmPath -Algorithm SHA256).Hash -ne $pmHash) {
    Remove-Item $pmPath
    throw "PresentMon.exe hash mismatch; refusing to bundle it."
}

# 2c. The PawnIO driver is GPL-2.0: put its exact matching source next to the installer so each
#     release can attach it (dist\ is where release files go). Its setup program is closed-source
#     freeware with no source to ship.
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$pawnSrc = "$root\dist\PawnIO-2.2.0-source.zip"
if (-not (Test-Path $pawnSrc)) {
    Write-Host "Downloading PawnIO 2.2.0 driver source (GPL compliance)..."
    Invoke-WebRequest "https://github.com/namazso/PawnIO/archive/refs/tags/2.2.0.zip" -OutFile $pawnSrc
}

# 3. Wizard images from the logo (Inno needs BMPs).
Add-Type -AssemblyName System.Drawing
$build = "$root\installer\build"
New-Item -ItemType Directory -Force $build | Out-Null
$logo = [System.Drawing.Image]::FromFile("$root\assets\toasty.png")
function Save-WizardImage([int]$w, [int]$h, [int]$logoSize, [System.Drawing.Color]$bg, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear($bg)
    $g.DrawImage($logo, [int](($w - $logoSize) / 2), [int](($h - $logoSize) / 2), $logoSize, $logoSize)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}
Save-WizardImage 328 628 320 ([System.Drawing.Color]::FromArgb(255, 247, 230)) "$build\wizard-large.bmp"
Save-WizardImage 110 110 108 ([System.Drawing.Color]::White) "$build\wizard-small.bmp"
$logo.Dispose()

# 4. Compile the installer. Inno Setup's compiler comes from its NuGet package, unpacked into the
#    project (installer\tools), so nothing gets installed on the build machine.
$innoVersion = '6.7.3'
$innoHash = 'F780898E402FF80612CC8D9FCB8C6E02932BD1CB4C900FFDAA31F9341CFB49F4'
$innoDir = "$root\installer\tools\innosetup-$innoVersion"
$iscc = "$innoDir\tools\ISCC.exe"
if (-not (Test-Path $iscc)) {
    $pkg = "$root\installer\tools\tools.innosetup.$innoVersion.nupkg"
    if (-not (Test-Path $pkg) -or (Get-FileHash $pkg -Algorithm SHA256).Hash -ne $innoHash) {
        Write-Host "Downloading Inno Setup $innoVersion (build tool)..."
        New-Item -ItemType Directory -Force (Split-Path $pkg) | Out-Null
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$innoVersion/tools.innosetup.$innoVersion.nupkg" -OutFile $pkg
    }
    if ((Get-FileHash $pkg -Algorithm SHA256).Hash -ne $innoHash) { throw "Inno Setup package hash mismatch." }
    Expand-Archive $pkg -DestinationPath $innoDir -Force
}
& $iscc "/DAppVersion=$version" "$root\installer\toasty.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed" }

Get-Item "$root\dist\Toasty-Setup-$version.exe" | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
