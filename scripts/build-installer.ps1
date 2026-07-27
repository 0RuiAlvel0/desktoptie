param(
    [string]$Version
)

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

if ([string]::IsNullOrWhiteSpace($Version))
{
    $Version = Get-BuildVersion -PropsPath $versionPropsPath
}

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

if (Test-Path $msiSourcePath)
{
    Remove-Item -Path $msiSourcePath -Force
}

if (Test-Path $msiTargetPath)
{
    Remove-Item -Path $msiTargetPath -Force
}

Write-Host "Publishing DesktopTie version $Version..."
dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Building MSI installer version $Version..."
dotnet build $installerProject -c Release -p:Version=$Version
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet build (installer) failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $msiSourcePath))
{
    throw "Expected MSI was not found at: $msiSourcePath"
}

Copy-Item -Path $msiSourcePath -Destination $msiTargetPath -Force
Write-Host "Installer created: $msiTargetPath"
