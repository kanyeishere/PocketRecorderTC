param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$FfmpegRoot = "",
    [string]$AmfSdkDir = "",
    [string]$VideoCodecSdkDir = "",
    [string]$VideoCodecSdkLibDir = "",
    [string]$OneVplSdkDir = "",
    [string]$NvencSdkVersionMajor = "",
    [string]$NvencSdkVersionMinor = "",
    [string]$NvencSdkVersionPatch = ""
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

function Read-NvencSdkVersion {
    param([string]$SdkDir)

    $result = @{
        Major = ""
        Minor = ""
        Patch = ""
    }

    if ([string]::IsNullOrWhiteSpace($SdkDir) -or -not (Test-Path -LiteralPath $SdkDir)) {
        return $result
    }

    $header = Join-Path $SdkDir "Interface\nvEncodeAPI.h"
    if (Test-Path -LiteralPath $header) {
        $text = Get-Content -LiteralPath $header -Raw
        $majorMatch = [regex]::Match($text, '(?m)^\s*#define\s+NVENCAPI_MAJOR_VERSION\s+(\d+)')
        $minorMatch = [regex]::Match($text, '(?m)^\s*#define\s+NVENCAPI_MINOR_VERSION\s+(\d+)')
        if ($majorMatch.Success) {
            $result.Major = $majorMatch.Groups[1].Value
        }
        if ($minorMatch.Success) {
            $result.Minor = $minorMatch.Groups[1].Value
        }
    }

    $name = Split-Path -Path $SdkDir -Leaf
    $versionMatch = [regex]::Match($name, 'Video_Codec_SDK_(\d+)\.(\d+)\.(\d+)')
    if ($versionMatch.Success) {
        if ([string]::IsNullOrWhiteSpace($result.Major)) {
            $result.Major = $versionMatch.Groups[1].Value
        }
        if ([string]::IsNullOrWhiteSpace($result.Minor)) {
            $result.Minor = $versionMatch.Groups[2].Value
        }
        $result.Patch = $versionMatch.Groups[3].Value
    }

    return $result
}

$sdkVersion = Read-NvencSdkVersion -SdkDir $VideoCodecSdkDir
if ([string]::IsNullOrWhiteSpace($NvencSdkVersionMajor)) {
    $NvencSdkVersionMajor = $sdkVersion.Major
}
if ([string]::IsNullOrWhiteSpace($NvencSdkVersionMinor)) {
    $NvencSdkVersionMinor = $sdkVersion.Minor
}
if ([string]::IsNullOrWhiteSpace($NvencSdkVersionPatch)) {
    $NvencSdkVersionPatch = $sdkVersion.Patch
}

if (-not [string]::IsNullOrWhiteSpace($VideoCodecSdkDir)) {
    if ([string]::IsNullOrWhiteSpace($sdkVersion.Major)) {
        throw "Unable to read NVENC API version from VideoCodecSdkDir '$VideoCodecSdkDir'. Set NativeRecorderNvenc12SdkDir or NativeRecorderVideoCodecSdkDir to a NVIDIA Video Codec SDK 12.x root."
    }

    if ($sdkVersion.Major -ne "12") {
        throw "NativeRecorder must be built with NVIDIA Video Codec SDK 12.x for the single ABI13 DLL. '$VideoCodecSdkDir' reports NVENC API $($sdkVersion.Major).$($sdkVersion.Minor)."
    }
}

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

if (-not [string]::IsNullOrWhiteSpace($VideoCodecSdkLibDir)) {
    $msbuildArgs += "/p:VideoCodecSdkLibDir=$VideoCodecSdkLibDir"
}

if (-not [string]::IsNullOrWhiteSpace($OneVplSdkDir)) {
    $msbuildArgs += "/p:OneVplSdkDir=$OneVplSdkDir"
}

if (-not [string]::IsNullOrWhiteSpace($NvencSdkVersionMajor)) {
    $msbuildArgs += "/p:NvencSdkVersionMajor=$NvencSdkVersionMajor"
}

if (-not [string]::IsNullOrWhiteSpace($NvencSdkVersionMinor)) {
    $msbuildArgs += "/p:NvencSdkVersionMinor=$NvencSdkVersionMinor"
}

if (-not [string]::IsNullOrWhiteSpace($NvencSdkVersionPatch)) {
    $msbuildArgs += "/p:NvencSdkVersionPatch=$NvencSdkVersionPatch"
}

& $msbuild @msbuildArgs
exit $LASTEXITCODE
