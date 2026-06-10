param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "csharp\ThinkBookFanControl\ThinkBookFanControl.csproj"
$selfContainedPublishDir = Join-Path $root "dist\ThinkBookFanControl-win-x64"
$frameworkDependentPublishDir = Join-Path $root "dist\ThinkBookFanControl-win-x64-net9-runtime"
$publishBuildDir = Join-Path $root ".tmp\csharp-publish-bin"
$legacyOutputDir = Join-Path $root "csharp\ThinkBookFanControl\bin\$Configuration\net9.0-windows\win-x64"

function Remove-SafeDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $fullRoot = [System.IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside the workspace: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

$info = dotnet --info 2>&1 | Out-String
if ($info -match "No SDKs were found") {
    throw "No .NET SDK is installed. Install the .NET 9 SDK, then re-run this script."
}

$running = Get-Process -Name "ThinkBookFanControl" -ErrorAction SilentlyContinue |
    Where-Object { (-not $_.Path) -or ($_.Path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) }
if ($running) {
    throw "ThinkBookFanControl.exe is running. Close all ThinkBookFanControl windows before building."
}

if ($Publish) {
    Remove-SafeDirectory $selfContainedPublishDir
    Remove-SafeDirectory $frameworkDependentPublishDir
    Remove-SafeDirectory $publishBuildDir
    Remove-SafeDirectory $legacyOutputDir
    dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $selfContainedPublishDir /p:PublishSingleFile=false "/p:BaseOutputPath=$publishBuildDir/"
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $frameworkDependentPublishDir /p:PublishSingleFile=false "/p:BaseOutputPath=$publishBuildDir/"
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    Write-Host "Publish output (self-contained): $selfContainedPublishDir"
    Write-Host "Publish output (.NET 9 runtime required): $frameworkDependentPublishDir"
} else {
    dotnet build $project -c $Configuration
}

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
