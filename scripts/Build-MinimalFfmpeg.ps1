param(
    [string] $FfmpegRepo = $env:FFMPEG_REPO,
    [string] $FfmpegRef = $env:FFMPEG_REF,
    [string] $OutputRoot,
    [string] $SourceDir,
    [string] $BuildDir,
    [switch] $Force
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$depsRoot = Join-Path $repoRoot "lib\native"

if ([string]::IsNullOrWhiteSpace($FfmpegRepo)) {
    $FfmpegRepo = "https://github.com/FFmpeg/FFmpeg.git"
}

if ([string]::IsNullOrWhiteSpace($FfmpegRef)) {
    $FfmpegRef = "n8.1.2"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $depsRoot "ffmpeg-minimal-lgpl-shared"
}

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Join-Path $depsRoot "_ffmpeg_source"
}

if ([string]::IsNullOrWhiteSpace($BuildDir)) {
    $BuildDir = Join-Path $depsRoot "_ffmpeg_minimal_build"
}

$prefixDir = Join-Path $OutputRoot "ffmpeg-minimal-win64-lgpl-shared"

function Test-MinimalFfmpegRoot([string] $root) {
    if (-not (Test-Path $root)) { return $false }

    $required = @(
        "include\libavformat\avformat.h",
        "include\libavcodec\avcodec.h",
        "include\libavutil\avutil.h",
        "include\libswresample\swresample.h",
        "lib\avformat.lib",
        "lib\avcodec.lib",
        "lib\avutil.lib",
        "lib\swresample.lib"
    )

    foreach ($relativePath in $required) {
        if (-not (Test-Path (Join-Path $root $relativePath))) {
            return $false
        }
    }

    foreach ($pattern in @("avformat-*.dll", "avcodec-*.dll", "avutil-*.dll", "swresample-*.dll")) {
        if (@(Get-ChildItem (Join-Path $root "bin") -Filter $pattern -File -ErrorAction SilentlyContinue).Count -eq 0) {
            return $false
        }
    }

    return $true
}

function Find-MsysBash {
    function Test-MsysBash([string] $bashPath) {
        if ([string]::IsNullOrWhiteSpace($bashPath) -or -not (Test-Path $bashPath)) {
            return $false
        }

        & $bashPath -lc 'export PATH="/mingw64/bin:/usr/bin:$PATH"; test -x /usr/bin/cygpath && test -d /mingw64 && command -v gcc >/dev/null 2>&1 && command -v make >/dev/null 2>&1' 1>$null 2>$null
        return $LASTEXITCODE -eq 0
    }

    $candidates = @(
        $env:MSYS2_BASH,
        "C:\msys64\usr\bin\bash.exe",
        "C:\msys64\usr\bin\bash"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-MsysBash $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    $bash = Get-Command bash.exe -ErrorAction SilentlyContinue
    if ($null -ne $bash -and (Test-MsysBash $bash.Source)) {
        return $bash.Source
    }

    throw "MSYS2 MINGW64 bash with cygpath, gcc, and make was not found. Install MSYS2 packages, or set MSYS2_BASH to C:\msys64\usr\bin\bash.exe."
}

function Find-LibExe {
    $existing = Get-Command lib.exe -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        return $existing.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (-not [string]::IsNullOrWhiteSpace($installPath)) {
            $match = Get-ChildItem (Join-Path $installPath "VC\Tools\MSVC") -Recurse -Filter lib.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match "\\bin\\Hostx64\\x64\\lib\.exe$" } |
                Select-Object -First 1
            if ($null -ne $match) {
                return $match.FullName
            }
        }
    }

    $programFiles = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
        $match = Get-ChildItem (Join-Path $programFiles "Microsoft Visual Studio") -Recurse -Filter lib.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\bin\\Hostx64\\x64\\lib\.exe$" } |
            Select-Object -First 1
        if ($null -ne $match) {
            return $match.FullName
        }
    }

    throw "MSVC lib.exe was not found. Install Visual Studio C++ build tools."
}

function Find-DumpbinExe([string] $libExePath) {
    if (-not [string]::IsNullOrWhiteSpace($libExePath)) {
        $candidate = Join-Path (Split-Path $libExePath -Parent) "dumpbin.exe"
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $existing = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        return $existing.Source
    }

    throw "MSVC dumpbin.exe was not found. Install Visual Studio C++ build tools."
}

if ((-not $Force) -and (Test-MinimalFfmpegRoot $prefixDir)) {
    Write-Host "Minimal FFmpeg already exists: $prefixDir"
    exit 0
}

$bash = Find-MsysBash
$libExe = Find-LibExe
$dumpbinExe = Find-DumpbinExe $libExe

New-Item -ItemType Directory -Force -Path $depsRoot, $OutputRoot | Out-Null

if (-not (Test-Path (Join-Path $SourceDir ".git"))) {
    if (Test-Path $SourceDir) {
        Remove-Item -Recurse -Force $SourceDir
    }

    New-Item -ItemType Directory -Force -Path $SourceDir | Out-Null
    git -C $SourceDir init
    git -C $SourceDir remote add origin $FfmpegRepo
}
else {
    git -C $SourceDir remote set-url origin $FfmpegRepo
}

Write-Host "Fetching FFmpeg source: $FfmpegRepo @ $FfmpegRef"
git -C $SourceDir fetch --depth 1 origin $FfmpegRef
git -C $SourceDir checkout --force FETCH_HEAD
git -C $SourceDir clean -fdx
$resolvedRef = (git -C $SourceDir rev-parse HEAD).Trim()

if (Test-Path $BuildDir) {
    Remove-Item -Recurse -Force $BuildDir
}
New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

$buildScript = Join-Path $BuildDir "build-minimal-ffmpeg.sh"
$bashScript = @'
set -euo pipefail

export MSYSTEM=MINGW64
export CHERE_INVOKING=1
export PATH="/mingw64/bin:/usr/bin:$PATH"

SRC="$(cygpath -u "$FFMPEG_SOURCE_DIR")"
BUILD="$(cygpath -u "$FFMPEG_BUILD_DIR")"
PREFIX="$(cygpath -u "$FFMPEG_PREFIX_DIR")"
JOBS="${FFMPEG_BUILD_JOBS:-2}"

rm -rf "$BUILD" "$PREFIX"
mkdir -p "$BUILD" "$PREFIX"
cd "$BUILD"

"$SRC/configure" \
  --prefix="$PREFIX" \
  --target-os=mingw32 \
  --arch=x86_64 \
  --enable-shared \
  --disable-static \
  --disable-programs \
  --disable-doc \
  --disable-debug \
  --disable-network \
  --disable-autodetect \
  --disable-everything \
  --disable-x86asm \
  --disable-pthreads \
  --enable-w32threads \
  --enable-small \
  --extra-ldflags="-static-libgcc" \
  --enable-avcodec \
  --enable-avformat \
  --enable-avutil \
  --enable-swresample \
  --enable-protocol=file \
  --enable-muxer=mp4 \
  --enable-encoder=aac \
  --enable-parser=aac \
  --enable-parser=h264 \
  --enable-parser=hevc \
  --enable-bsf=aac_adtstoasc \
  --enable-bsf=extract_extradata \
  --enable-bsf=h264_mp4toannexb \
  --enable-bsf=hevc_mp4toannexb

make -j"$JOBS"
make install
'@
[System.IO.File]::WriteAllText($buildScript, $bashScript, [System.Text.UTF8Encoding]::new($false))

$env:FFMPEG_SOURCE_DIR = (Resolve-Path $SourceDir).Path
$env:FFMPEG_BUILD_DIR = (Resolve-Path $BuildDir).Path
$env:FFMPEG_PREFIX_DIR = $prefixDir
if ([string]::IsNullOrWhiteSpace($env:FFMPEG_BUILD_JOBS)) {
    $env:FFMPEG_BUILD_JOBS = if ([string]::IsNullOrWhiteSpace($env:NUMBER_OF_PROCESSORS)) { "2" } else { $env:NUMBER_OF_PROCESSORS }
}
$env:FFMPEG_BUILD_SCRIPT = $buildScript

Write-Host "Building minimal LGPL FFmpeg into $prefixDir"
$buildScriptUnix = (& $bash -lc 'cygpath -u "$FFMPEG_BUILD_SCRIPT"').Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($buildScriptUnix)) {
    throw "Failed to convert FFmpeg build script path for MSYS2 bash: $buildScript"
}
$env:FFMPEG_BUILD_SCRIPT_UNIX = $buildScriptUnix
& $bash -lc 'bash "$FFMPEG_BUILD_SCRIPT_UNIX"'

foreach ($component in @("avformat", "avcodec", "avutil", "swresample")) {
    $def = Get-ChildItem (Join-Path $prefixDir "lib") -Filter "$component-*.def" -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $def) {
        throw "FFmpeg build did not install an import definition for $component."
    }

    $outLib = Join-Path $prefixDir "lib\$component.lib"
    & $libExe /nologo /machine:x64 "/def:$($def.FullName)" "/out:$outLib"
}

$runtimeDependencyNames = [ordered]@{}
foreach ($pattern in @("avformat-*.dll", "avcodec-*.dll", "avutil-*.dll", "swresample-*.dll")) {
    foreach ($dll in Get-ChildItem (Join-Path $prefixDir "bin") -Filter $pattern -File -ErrorAction SilentlyContinue) {
        $dependencies = & $dumpbinExe /DEPENDENTS $dll.FullName 2>&1
        foreach ($match in [regex]::Matches(($dependencies -join "`n"), "lib(?:gcc|stdc\+\+|winpthread)[^\s]*\.dll", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $runtimeDependencyNames[$match.Value] = $true
        }
    }
}

foreach ($runtimeDependencyName in $runtimeDependencyNames.Keys) {
    $runtimeDependency = Get-ChildItem "C:\msys64\mingw64\bin" -Filter $runtimeDependencyName -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $runtimeDependency) {
        throw "Minimal FFmpeg DLLs depend on $runtimeDependencyName, but it was not found in C:\msys64\mingw64\bin."
    }

    Copy-Item -Path $runtimeDependency.FullName -Destination (Join-Path $prefixDir "bin\$runtimeDependencyName") -Force
}

foreach ($licenseName in @("LICENSE.md", "COPYING.LGPLv2.1", "COPYING.LGPLv3", "COPYING.GPLv2", "COPYING.GPLv3")) {
    $licensePath = Join-Path $SourceDir $licenseName
    if (Test-Path $licensePath) {
        Copy-Item -Path $licensePath -Destination (Join-Path $prefixDir $licenseName) -Force
    }
}

$buildInfo = @"
FFmpeg minimal LGPL build
Repository: $FfmpegRepo
Ref: $FfmpegRef
ResolvedRef: $resolvedRef

Enabled components:
  libraries: avcodec, avformat, avutil, swresample
  muxers: mp4
  encoders: aac
  protocols: file
  parsers: aac, h264, hevc
  bitstream filters: aac_adtstoasc, extract_extradata, h264_mp4toannexb, hevc_mp4toannexb
"@
$buildInfo | Set-Content -Path (Join-Path $prefixDir "BUILDINFO.txt") -Encoding utf8

if (-not (Test-MinimalFfmpegRoot $prefixDir)) {
    throw "Minimal FFmpeg build completed, but required headers, import libs, or DLLs were not found."
}

Write-Host "Minimal FFmpeg ready: $prefixDir"
