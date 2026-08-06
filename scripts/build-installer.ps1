$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$installerProject = Join-Path $repoRoot "Installer\DesktopTie.Installer.wixproj"
$appProject = Join-Path $repoRoot "DesktopTie.csproj"
$versionPropsPath = Join-Path $repoRoot "Directory.Build.props"
$artifactsRoot = Join-Path $repoRoot "artifacts"
$publishDir = Join-Path $artifactsRoot "publish"
$installerDir = Join-Path $artifactsRoot "installer"

function Get-BuildVersion {
    param(
        [string]$PropsPath
    )

    if (-not (Test-Path $PropsPath))
    {
        throw "Version props file was not found at: $PropsPath"
    }

    [xml]$propsXml = Get-Content -Path $PropsPath
    $resolvedVersion = $propsXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($resolvedVersion))
    {
        throw "No <Version> value was found in $PropsPath"
    }

    return $resolvedVersion.Trim()
}

$Version = Get-BuildVersion -PropsPath $versionPropsPath

if ($Version -notmatch '^\d+\.\d+\.\d+$')
{
    throw "Version must be in format major.minor.patch (for example: 0.0.1)"
}

$msiSourcePath = Join-Path $repoRoot "Installer\bin\x64\Release\DesktopTie.msi"
$msiTargetPath = Join-Path $installerDir ("DesktopTie-{0}.msi" -f $Version)

if (Test-Path $publishDir)
{
    Remove-Item -Path $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

Write-Host "Publishing DesktopTie version $Version..."
dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Building MSI installer version $Version..."
dotnet build $installerProject -c Release
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet build (installer) failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $msiSourcePath))
{
    throw "Expected MSI was not found at: $msiSourcePath"
}

try
{
    Copy-Item -Path $msiSourcePath -Destination $msiTargetPath -Force
    Write-Host "Installer created: $msiTargetPath"
}
catch
{
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $fallbackTargetPath = Join-Path $installerDir ("DesktopTie-{0}-{1}.msi" -f $Version, $timestamp)
    Copy-Item -Path $msiSourcePath -Destination $fallbackTargetPath -Force
    Write-Warning "Primary installer artifact path was locked: $msiTargetPath"
    Write-Host "Installer created at fallback path: $fallbackTargetPath"
}
