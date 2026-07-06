param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$FfmpegRoot = "",
    [string]$AmfSdkDir = "",
    [string]$VideoCodecSdkDir = "",
    [string]$OneVplSdkDir = ""
)

$ErrorActionPreference = "Stop"

function Find-MSBuild {
    $command = Get-Command "msbuild.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ($programFilesX86) {
        $vswhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path -LiteralPath $vswhere) {
            $found = & $vswhere -latest -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find "MSBuild\**\Bin\MSBuild.exe" |
                Select-Object -First 1
            if ($found -and (Test-Path -LiteralPath $found)) {
                return $found
            }
        }

        $fallbacks = @(
            "Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
            "Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
            "Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
            "Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
        )

        foreach ($relativePath in $fallbacks) {
            $candidate = Join-Path $programFilesX86 $relativePath
            if (Test-Path -LiteralPath $candidate) {
                return $candidate
            }
        }

        if (Test-Path -LiteralPath $vswhere) {
            $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
                Select-Object -First 1
            if ($found -and (Test-Path -LiteralPath $found)) {
                return $found
            }
        }
    }

    throw "MSBuild.exe was not found. Install Visual Studio Build Tools with C++ workload, or run from a Developer PowerShell."
}

$msbuild = Find-MSBuild
$projectFullPath = (Resolve-Path -LiteralPath $ProjectPath).Path

$msbuildArgs = @(
    $projectFullPath,
    "/t:Build",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/m",
    "/v:minimal"
)

if (-not [string]::IsNullOrWhiteSpace($FfmpegRoot)) {
    $msbuildArgs += "/p:FfmpegRoot=$FfmpegRoot"
}

if (-not [string]::IsNullOrWhiteSpace($AmfSdkDir)) {
    $msbuildArgs += "/p:AmfSdkDir=$AmfSdkDir"
}

if (-not [string]::IsNullOrWhiteSpace($VideoCodecSdkDir)) {
    $msbuildArgs += "/p:VideoCodecSdkDir=$VideoCodecSdkDir"
}

if (-not [string]::IsNullOrWhiteSpace($OneVplSdkDir)) {
    $msbuildArgs += "/p:OneVplSdkDir=$OneVplSdkDir"
}

& $msbuild @msbuildArgs
exit $LASTEXITCODE
