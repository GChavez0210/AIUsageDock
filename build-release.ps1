$ErrorActionPreference = "Stop"

# Builds a signed MSIX for distribution. Output lands in .\dist:
#   AIUsageDock-<version>-x64.msix   the package
#   AIUsageDock-Dev.cer              the public cert users must trust before installing
#
# Signing uses a self-signed cert with subject CN=AIUsageDock-Dev (the manifest
# Publisher). The script creates one in Cert:\CurrentUser\My if none exists.

$projectDirectory = Join-Path $PSScriptRoot "AIUsageDock"
$project = Join-Path $projectDirectory "AIUsageDock.csproj"
$manifestPath = Join-Path $projectDirectory "Package.appxmanifest"
$targetFramework = "net10.0-windows10.0.26100.0"
$runtime = "win-x64"
$publish = Join-Path $projectDirectory "bin\Release\$targetFramework\$runtime\publish"
$staging = Join-Path $projectDirectory "bin\msix-release-staging"
$dist = Join-Path $PSScriptRoot "dist"

$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Where-Object Name -match '^10\.' | Sort-Object Name -Descending | Select-Object -First 1
$makeappx = Join-Path $sdkBin.FullName "x64\makeappx.exe"
$signtool = Join-Path $sdkBin.FullName "x64\signtool.exe"
if (-not (Test-Path $makeappx) -or -not (Test-Path $signtool)) {
    throw "makeappx.exe / signtool.exe not found under the Windows SDK. Install the Windows 10/11 SDK."
}

[xml]$manifest = Get-Content -LiteralPath $manifestPath
$version = $manifest.Package.Identity.Version
$publisher = $manifest.Package.Identity.Publisher
$packageName = "AIUsageDock-$version-x64.msix"

Write-Host "[1/5] Build and test"
& (Join-Path $PSScriptRoot "build.ps1")

Write-Host "[2/5] Publish $version"
dotnet publish $project -c Release -r $runtime --self-contained false -p:GenerateAppxPackageOnBuild=false -v minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "[3/5] Stage package payload"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -Path $staging -ItemType Directory | Out-Null
Copy-Item -Path (Join-Path $publish "*") -Destination $staging -Recurse -Force
$stagingAssets = Join-Path $staging "Assets"
if (-not (Test-Path -LiteralPath $stagingAssets)) {
    New-Item -Path $stagingAssets -ItemType Directory | Out-Null
}
Copy-Item -Path (Join-Path $projectDirectory "Assets\*") -Destination $stagingAssets -Recurse -Force
# The app extension declares PublicFolder="Public", which has to exist in the
# package. makeappx skips empty directories, so the folder needs a file.
New-Item -Path (Join-Path $staging "Public") -ItemType Directory | Out-Null
Set-Content -Path (Join-Path $staging "Public\README.txt") -Value "Public folder for the Command Palette app extension."

# The payload is win-x64, so say so rather than shipping a neutral package.
$stagedManifest = Join-Path $staging "AppxManifest.xml"
$manifest.Package.Identity.SetAttribute("ProcessorArchitecture", "x64")
$manifest.Save($stagedManifest)

Write-Host "[4/5] Pack $packageName"
if (-not (Test-Path -LiteralPath $dist)) { New-Item -Path $dist -ItemType Directory | Out-Null }
$package = Join-Path $dist $packageName
if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
& $makeappx pack /d $staging /p $package /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

Write-Host "[5/5] Sign"
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host "No signing certificate for $publisher found; creating a self-signed one."
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher `
        -KeyUsage DigitalSignature -FriendlyName "AI Usage Dock dev signing" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -NotAfter (Get-Date).AddYears(3)
}
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $package
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }
Export-Certificate -Cert $cert -FilePath (Join-Path $dist "AIUsageDock-Dev.cer") -Force | Out-Null

Write-Host ""
Write-Host "Built $package"
Write-Host "Signed with $($cert.Subject) ($($cert.Thumbprint))"
Write-Host "Users must import dist\AIUsageDock-Dev.cer into 'Trusted People' (Local Machine) before installing the MSIX."
