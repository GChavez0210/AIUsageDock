$ErrorActionPreference = "Stop"

$projectDirectory = Join-Path $PSScriptRoot "AIUsageDock"
$project = Join-Path $projectDirectory "AIUsageDock.csproj"
$targetFramework = "net10.0-windows10.0.26100.0"
$runtime = "win-x64"
$bin = Join-Path $projectDirectory "bin"
$publish = Join-Path $bin "Release\$targetFramework\$runtime\publish"
$staging = Join-Path $bin "msix-staging"

Write-Host "[1/5] Run tests"
& (Join-Path $PSScriptRoot "build.ps1")

Write-Host "[2/5] Publish extension"
dotnet publish $project -c Release -r $runtime --self-contained false -p:GenerateAppxPackageOnBuild=false -v minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "[3/5] Stage package payload"
Get-Process -Name "AIUsageDock", "Microsoft.CmdPal.UI" -ErrorAction SilentlyContinue | Stop-Process -Force
$existingPackage = Get-AppxPackage -Name "Gabox.AIUsageDock"
if ($existingPackage) {
    Remove-AppxPackage -Package $existingPackage.PackageFullName
}
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -Path $staging -ItemType Directory | Out-Null
Copy-Item -Path (Join-Path $publish "*") -Destination $staging -Recurse -Force
$stagingAssets = Join-Path $staging "Assets"
if (-not (Test-Path -LiteralPath $stagingAssets)) {
    New-Item -Path $stagingAssets -ItemType Directory | Out-Null
}
Copy-Item -Path (Join-Path $projectDirectory "Assets\*") -Destination $stagingAssets -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectDirectory "Package.appxmanifest") -Destination (Join-Path $staging "AppxManifest.xml") -Force

Write-Host "[4/5] Register development package"
$manifest = Join-Path $staging "AppxManifest.xml"
Add-AppxPackage -Register $manifest -ForceApplicationShutdown

Write-Host "[5/5] Restart Command Palette"
Start-Sleep -Seconds 2
Start-Process explorer.exe "shell:AppsFolder\Microsoft.CommandPalette_8wekyb3d8bbwe!App"

$installed = Get-AppxPackage -Name "Gabox.AIUsageDock"
if (-not $installed) { throw "AI Usage Dock was not found after installation" }

Write-Host "Installed AI Usage Dock $($installed.Version)."
Write-Host "Enable its bands under Command Palette Settings -> Dock -> Bands."
