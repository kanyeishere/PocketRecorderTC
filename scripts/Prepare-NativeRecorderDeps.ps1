param(
    [string] $FfmpegUrl = $env:FFMPEG_URL,
    [string] $FfmpegRootOverride = $env:NATIVE_RECORDER_FFMPEG_ROOT,
    [string] $UseMinimalFfmpeg = $env:USE_MINIMAL_FFMPEG,
    [string] $VideoCodecSdkUrl = $env:VIDEO_CODEC_SDK_URL,
    [string] $VideoCodecSdk12Url = $env:VIDEO_CODEC_SDK_12_URL,
    [string] $AmfRepo = $env:AMF_REPO,
    [string] $AmfRef = $env:AMF_REF,
    [string] $VideoCodecSdkDir = $(if (-not [string]::IsNullOrWhiteSpace($env:NativeRecorderVideoCodecSdkDir)) { $env:NativeRecorderVideoCodecSdkDir } else { $env:VideoCodecSdkDir }),
    [string] $VideoCodecSdk12Dir = $env:NativeRecorderNvenc12SdkDir,
    [string] $OneVplVersion = $env:ONEVPL_VERSION,
    [string] $OneVplDevelUrl = $env:ONEVPL_DEVEL_URL,
    [string] $OneVplRuntimeUrl = $env:ONEVPL_RUNTIME_URL,
    [string] $OneVplSdkDir = $env:OneVplSdkDir,
    [string] $OneVplRuntimeDir = $env:OneVplRuntimeDir
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$depsRoot = Join-Path $repoRoot "lib\native"
$ffmpegRoot = Join-Path $depsRoot "ffmpeg-btbn-lgpl-shared"
$minimalFfmpegRoot = Join-Path $depsRoot "ffmpeg-minimal-lgpl-shared"
$amfRoot = Join-Path $depsRoot "AMF"
$videoCodecSdkRoot = Join-Path $depsRoot "VideoCodecSDK"
$oneVplRoot = Join-Path $depsRoot "oneVPL"
$propsPath = Join-Path $depsRoot "NativeRecorderDeps.props"

if ([string]::IsNullOrWhiteSpace($FfmpegUrl)) {
    $FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl-shared.zip"
}

if ([string]::IsNullOrWhiteSpace($AmfRepo)) {
    $AmfRepo = "https://github.com/GPUOpen-LibrariesAndSDKs/AMF.git"
}

if ([string]::IsNullOrWhiteSpace($AmfRef)) {
    $AmfRef = "6ec029531e356102aafe1e236cfd0ddf739939da"
}

if ([string]::IsNullOrWhiteSpace($OneVplVersion)) {
    try {
        $oneVplIndex = Invoke-RestMethod -Uri "https://api.nuget.org/v3-flatcontainer/onevpl.devel.win-x64/index.json" -UseBasicParsing
        $OneVplVersion = $oneVplIndex.versions[-1]
    }
    catch {
        $OneVplVersion = "2023.1.0.43419"
    }
}

if ([string]::IsNullOrWhiteSpace($OneVplDevelUrl)) {
    $OneVplDevelUrl = "https://api.nuget.org/v3-flatcontainer/onevpl.devel.win-x64/$OneVplVersion/onevpl.devel.win-x64.$OneVplVersion.nupkg"
}

if ([string]::IsNullOrWhiteSpace($OneVplRuntimeUrl)) {
    $OneVplRuntimeUrl = "https://api.nuget.org/v3-flatcontainer/onevpl.runtime.win-x64/$OneVplVersion/onevpl.runtime.win-x64.$OneVplVersion.nupkg"
}

New-Item -ItemType Directory -Force -Path $depsRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($VideoCodecSdk12Dir)) {
    $VideoCodecSdk12Dir = $env:NATIVE_RECORDER_NVENC12_SDK_DIR
}

function Test-FFmpegRoot([string] $root) {
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

function Find-FFmpegRoot([string] $root) {
    if (-not (Test-Path $root)) { return $null }

    $rootItem = Get-Item $root
    $candidates = @($rootItem) + @(Get-ChildItem $root -Directory -Recurse)

    return $candidates |
        Where-Object { Test-FFmpegRoot $_.FullName } |
        Select-Object -First 1
}

function Find-VideoCodecSdkRoot([string] $root) {
    if (-not (Test-Path $root)) { return $null }

    $rootItem = Get-Item $root
    $candidates = @($rootItem) + @(Get-ChildItem $root -Directory -Recurse)

    return $candidates |
        Where-Object {
            (Test-Path (Join-Path $_.FullName "Interface\nvEncodeAPI.h")) -and
            (Test-Path (Join-Path $_.FullName "Samples\NvCodec\NvEncoder\NvEncoderD3D11.h")) -and
            (
                (Test-Path (Join-Path $_.FullName "Lib\win\x64\nvencodeapi.lib")) -or
                (Test-Path (Join-Path $_.FullName "Lib\x64\nvencodeapi.lib"))
            )
        } |
        Select-Object -First 1
}

function Get-NvencApiMajor([string] $sdkRoot) {
    $header = Join-Path $sdkRoot "Interface\nvEncodeAPI.h"
    if (-not (Test-Path $header)) { return $null }

    $text = Get-Content -LiteralPath $header -Raw
    $match = [regex]::Match($text, '(?m)^\s*#define\s+NVENCAPI_MAJOR_VERSION\s+(\d+)')
    if (-not $match.Success) { return $null }

    return [int]$match.Groups[1].Value
}

function Find-Nvenc12SdkRoot([string] $root) {
    if (-not (Test-Path $root)) { return $null }

    $rootItem = Get-Item $root
    $candidates = @($rootItem) + @(Get-ChildItem $root -Directory -Recurse)

    foreach ($candidateRoot in $candidates) {
        $candidate = Find-VideoCodecSdkRoot $candidateRoot.FullName
        if ($null -ne $candidate -and (Get-NvencApiMajor $candidate.FullName) -eq 12) {
            return $candidate
        }
    }

    return $null
}

function Test-OneVplSdkRoot([string] $root) {
    if (-not (Test-Path $root)) { return $false }

    $required = @(
        "lib\native\include\vpl\mfxvideo.h",
        "lib\native\include\vpl\mfxdispatcher.h",
        "lib\native\win-x64\vpl.lib"
    )

    foreach ($relativePath in $required) {
        if (-not (Test-Path (Join-Path $root $relativePath))) {
            return $false
        }
    }

    return $true
}

function Find-OneVplSdkRoot([string] $root) {
    if (-not (Test-Path $root)) { return $null }

    $rootItem = Get-Item $root
    $candidates = @($rootItem) + @(Get-ChildItem $root -Directory -Recurse)

    return $candidates |
        Where-Object { Test-OneVplSdkRoot $_.FullName } |
        Select-Object -First 1
}

function Test-OneVplRuntimeRoot([string] $root) {
    if (-not (Test-Path $root)) { return $false }
    return Test-Path (Join-Path $root "libvpl.dll")
}

function Find-OneVplRuntimeRoot([string] $root) {
    if (-not (Test-Path $root)) { return $null }

    $rootItem = Get-Item $root
    $candidates = @($rootItem) + @(Get-ChildItem $root -Directory -Recurse)

    return $candidates |
        Where-Object { Test-OneVplRuntimeRoot $_.FullName } |
        Select-Object -First 1
}

function Set-CiEnv([string] $name, [string] $value) {
    Set-Item -Path "env:$name" -Value $value
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
        "$name=$value" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
    }
}

function Test-Truthy([string] $value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return $false }

    return @("1", "true", "yes", "on") -contains $value.Trim().ToLowerInvariant()
}

$ffmpegDir = $null
if (-not [string]::IsNullOrWhiteSpace($FfmpegRootOverride)) {
    $ffmpegDir = Find-FFmpegRoot $FfmpegRootOverride
    if ($null -eq $ffmpegDir) {
        throw "NATIVE_RECORDER_FFMPEG_ROOT was set, but no usable FFmpeg root was found under: $FfmpegRootOverride"
    }
}

if ($null -eq $ffmpegDir -and (Test-Truthy $UseMinimalFfmpeg)) {
    $ffmpegDir = Find-FFmpegRoot $minimalFfmpegRoot
    if ($null -eq $ffmpegDir) {
        throw "Minimal FFmpeg was requested, but no usable build was found under $minimalFfmpegRoot. Run scripts\Build-MinimalFfmpeg.ps1 locally and commit lib/native/ffmpeg-minimal-lgpl-shared, or unset USE_MINIMAL_FFMPEG to use the BtbN package."
    }
}

if ($null -eq $ffmpegDir) {
    $ffmpegDir = Find-FFmpegRoot $ffmpegRoot
}
if ($null -eq $ffmpegDir) {
    $ffmpegZip = Join-Path $depsRoot "ffmpeg-btbn-lgpl-shared.zip"
    Write-Host "Downloading FFmpeg shared package: $FfmpegUrl"
    Invoke-WebRequest -Uri $FfmpegUrl -OutFile $ffmpegZip -UseBasicParsing
    New-Item -ItemType Directory -Force -Path $ffmpegRoot | Out-Null
    Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegRoot -Force
    $ffmpegDir = Find-FFmpegRoot $ffmpegRoot
}
if ($null -eq $ffmpegDir) {
    throw "FFmpeg shared package was prepared, but libavformat headers/libs/DLLs were not found."
}

if (-not (Test-Path (Join-Path $amfRoot "amf\public"))) {
    if (Test-Path $amfRoot) {
        Remove-Item -Recurse -Force $amfRoot
    }

    $amfCheckout = Join-Path $depsRoot "_amf_checkout"
    if (Test-Path $amfCheckout) {
        Remove-Item -Recurse -Force $amfCheckout
    }

    Write-Host "Cloning AMF SDK: $AmfRepo @ $AmfRef"
    git clone --depth 1 $AmfRepo $amfCheckout
    git -C $amfCheckout checkout $AmfRef
    New-Item -ItemType Directory -Force -Path $amfRoot | Out-Null
    Copy-Item -Path (Join-Path $amfCheckout "amf") -Destination $amfRoot -Recurse -Force
    Copy-Item -Path (Join-Path $amfCheckout "LICENSE.txt") -Destination (Join-Path $amfRoot "LICENSE.txt") -Force
    Remove-Item -Recurse -Force $amfCheckout
}
$amfDir = Join-Path $amfRoot "amf"
if (-not (Test-Path (Join-Path $amfDir "public\common\AMFFactory.cpp"))) {
    throw "AMF SDK was prepared, but AMFFactory.cpp was not found."
}

$sdk12Dir = $null
if (-not [string]::IsNullOrWhiteSpace($VideoCodecSdk12Dir) -and (Test-Path $VideoCodecSdk12Dir)) {
    $sdk12Dir = Find-VideoCodecSdkRoot $VideoCodecSdk12Dir
    if ($null -ne $sdk12Dir -and (Get-NvencApiMajor $sdk12Dir.FullName) -ne 12) {
        Write-Warning "Ignoring NativeRecorderNvenc12SdkDir because it is not an NVENC API 12 SDK: $($sdk12Dir.FullName)"
        $sdk12Dir = $null
    }
}
if ($null -eq $sdk12Dir -and -not [string]::IsNullOrWhiteSpace($VideoCodecSdkDir) -and (Test-Path $VideoCodecSdkDir)) {
    $sdk12Dir = Find-Nvenc12SdkRoot $VideoCodecSdkDir
    if ($null -eq $sdk12Dir) {
        $candidate = Find-VideoCodecSdkRoot $VideoCodecSdkDir
        if ($null -ne $candidate) {
            Write-Warning "Ignoring NativeRecorderVideoCodecSdkDir because it is not an NVENC API 12 SDK: $($candidate.FullName)"
        }
    }
}
if ($null -eq $sdk12Dir) {
    $sdk12Dir = Find-Nvenc12SdkRoot $videoCodecSdkRoot
}
if ($null -eq $sdk12Dir -and -not [string]::IsNullOrWhiteSpace($VideoCodecSdk12Url)) {
    $sdk12Zip = Join-Path $depsRoot "Video_Codec_SDK_12.zip"
    $sdk12Root = Join-Path $videoCodecSdkRoot "nvenc12"
    Write-Host "Downloading NVIDIA Video Codec SDK 12."
    Invoke-WebRequest -Uri $VideoCodecSdk12Url -OutFile $sdk12Zip -UseBasicParsing
    if (Test-Path $sdk12Root) {
        Remove-Item -Recurse -Force $sdk12Root
    }
    New-Item -ItemType Directory -Force -Path $sdk12Root | Out-Null
    Expand-Archive -Path $sdk12Zip -DestinationPath $sdk12Root -Force
    $sdk12Dir = Find-Nvenc12SdkRoot $sdk12Root
}
if ($null -eq $sdk12Dir -and -not [string]::IsNullOrWhiteSpace($VideoCodecSdkUrl)) {
    $sdkZip = Join-Path $depsRoot "Video_Codec_SDK.zip"
    $sdkRoot = Join-Path $videoCodecSdkRoot "generic"
    Write-Host "Downloading NVIDIA Video Codec SDK from VIDEO_CODEC_SDK_URL. The archive must contain SDK 12.x."
    Invoke-WebRequest -Uri $VideoCodecSdkUrl -OutFile $sdkZip -UseBasicParsing
    if (Test-Path $sdkRoot) {
        Remove-Item -Recurse -Force $sdkRoot
    }
    New-Item -ItemType Directory -Force -Path $sdkRoot | Out-Null
    Expand-Archive -Path $sdkZip -DestinationPath $sdkRoot -Force
    $sdk12Dir = Find-Nvenc12SdkRoot $sdkRoot
}
if ($null -eq $sdk12Dir) {
    throw "NativeRecorder now builds the single ABI13 DLL with NVIDIA Video Codec SDK 12.x to keep older drivers supported. Set NativeRecorderVideoCodecSdkDir or NativeRecorderNvenc12SdkDir to a Video_Codec_SDK_12.x root, or set VIDEO_CODEC_SDK_12_URL in GitHub Actions."
}
$sdkDir = $sdk12Dir

$oneVplSdk = $null
if (-not [string]::IsNullOrWhiteSpace($OneVplSdkDir) -and (Test-Path $OneVplSdkDir)) {
    $oneVplSdk = Find-OneVplSdkRoot $OneVplSdkDir
}
if ($null -eq $oneVplSdk) {
    $oneVplSdk = Find-OneVplSdkRoot $oneVplRoot
}
if ($null -eq $oneVplSdk) {
    $oneVplDevelPackage = Join-Path $depsRoot "onevpl.devel.win-x64.$OneVplVersion.zip"
    $oneVplDevelRoot = Join-Path $oneVplRoot "devel"
    $oneVplDevelExtract = Join-Path $depsRoot "_onevpl_devel_extract"
    Write-Host "Downloading oneVPL development package: $OneVplDevelUrl"
    Invoke-WebRequest -Uri $OneVplDevelUrl -OutFile $oneVplDevelPackage -UseBasicParsing
    if (Test-Path $oneVplDevelRoot) {
        Remove-Item -Recurse -Force $oneVplDevelRoot
    }
    if (Test-Path $oneVplDevelExtract) {
        Remove-Item -Recurse -Force $oneVplDevelExtract
    }
    New-Item -ItemType Directory -Force -Path $oneVplDevelRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $oneVplDevelExtract | Out-Null
    Expand-Archive -Path $oneVplDevelPackage -DestinationPath $oneVplDevelExtract -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $oneVplDevelRoot "lib\native") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $oneVplDevelRoot "lib\native\win-x64") | Out-Null
    Copy-Item -Path (Join-Path $oneVplDevelExtract "lib\native\include") -Destination (Join-Path $oneVplDevelRoot "lib\native") -Recurse -Force
    $oneVplPreviewHeaders = Join-Path $oneVplDevelRoot "lib\native\include\vpl\preview"
    if (Test-Path $oneVplPreviewHeaders) {
        Remove-Item -Recurse -Force $oneVplPreviewHeaders
    }
    Copy-Item -Path (Join-Path $oneVplDevelExtract "lib\native\win-x64\vpl.lib") -Destination (Join-Path $oneVplDevelRoot "lib\native\win-x64\vpl.lib") -Force
    Copy-Item -Path (Join-Path $oneVplDevelExtract "license.txt") -Destination (Join-Path $oneVplDevelRoot "license.txt") -Force
    Remove-Item -Recurse -Force $oneVplDevelExtract
    $oneVplSdk = Find-OneVplSdkRoot $oneVplDevelRoot
}
if ($null -eq $oneVplSdk) {
    throw "oneVPL development package was prepared, but vpl headers/import library were not found."
}

$oneVplRuntime = $null
if (-not [string]::IsNullOrWhiteSpace($OneVplRuntimeDir) -and (Test-Path $OneVplRuntimeDir)) {
    $oneVplRuntime = Find-OneVplRuntimeRoot $OneVplRuntimeDir
}
if ($null -eq $oneVplRuntime) {
    $oneVplRuntime = Find-OneVplRuntimeRoot $oneVplRoot
}
if ($null -eq $oneVplRuntime) {
    $oneVplRuntimePackage = Join-Path $depsRoot "onevpl.runtime.win-x64.$OneVplVersion.zip"
    $oneVplRuntimeRoot = Join-Path $oneVplRoot "runtime"
    $oneVplRuntimeExtract = Join-Path $depsRoot "_onevpl_runtime_extract"
    Write-Host "Downloading oneVPL runtime package: $OneVplRuntimeUrl"
    Invoke-WebRequest -Uri $OneVplRuntimeUrl -OutFile $oneVplRuntimePackage -UseBasicParsing
    if (Test-Path $oneVplRuntimeRoot) {
        Remove-Item -Recurse -Force $oneVplRuntimeRoot
    }
    if (Test-Path $oneVplRuntimeExtract) {
        Remove-Item -Recurse -Force $oneVplRuntimeExtract
    }
    New-Item -ItemType Directory -Force -Path $oneVplRuntimeRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $oneVplRuntimeExtract | Out-Null
    Expand-Archive -Path $oneVplRuntimePackage -DestinationPath $oneVplRuntimeExtract -Force
    $oneVplRuntimeNativeSource = Join-Path $oneVplRuntimeExtract "runtimes\win-x64\native"
    $oneVplRuntimeNativeDest = Join-Path $oneVplRuntimeRoot "runtimes\win-x64\native"
    New-Item -ItemType Directory -Force -Path $oneVplRuntimeNativeDest | Out-Null
    foreach ($pattern in @("libvpl.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll")) {
        Copy-Item -Path (Join-Path $oneVplRuntimeNativeSource $pattern) -Destination $oneVplRuntimeNativeDest -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path (Join-Path $oneVplRuntimeExtract "vpl")) {
        Copy-Item -Path (Join-Path $oneVplRuntimeExtract "vpl") -Destination $oneVplRuntimeRoot -Recurse -Force
    }
    Copy-Item -Path (Join-Path $oneVplRuntimeExtract "license.txt") -Destination (Join-Path $oneVplRuntimeRoot "license.txt") -Force
    Remove-Item -Recurse -Force $oneVplRuntimeExtract
    $oneVplRuntime = Find-OneVplRuntimeRoot $oneVplRuntimeRoot
}
if ($null -eq $oneVplRuntime) {
    throw "oneVPL runtime package was prepared, but libvpl.dll was not found."
}

Set-CiEnv "NATIVE_RECORDER_FFMPEG_ROOT" $ffmpegDir.FullName
Set-CiEnv "NATIVE_RECORDER_AMF_DIR" $amfDir
Set-CiEnv "NATIVE_RECORDER_VIDEO_CODEC_SDK_DIR" $sdkDir.FullName
Set-CiEnv "NATIVE_RECORDER_ONEVPL_SDK_DIR" $oneVplSdk.FullName
Set-CiEnv "NATIVE_RECORDER_ONEVPL_RUNTIME_DIR" $oneVplRuntime.FullName

$props = @"
<Project>
  <PropertyGroup>
    <FfmpegRoot>$($ffmpegDir.FullName)</FfmpegRoot>
    <NativeRecorderFfmpegRoot>$($ffmpegDir.FullName)</NativeRecorderFfmpegRoot>
    <AmfSdkDir>$amfDir</AmfSdkDir>
    <VideoCodecSdkDir>$($sdkDir.FullName)</VideoCodecSdkDir>
    <OneVplSdkDir>$($oneVplSdk.FullName)</OneVplSdkDir>
    <OneVplRuntimeDir>$($oneVplRuntime.FullName)</OneVplRuntimeDir>
  </PropertyGroup>
</Project>
"@
$props | Set-Content -Path $propsPath -Encoding utf8

Write-Host "NativeRecorder dependency roots:"
Write-Host "  FFmpeg:           $($ffmpegDir.FullName)"
Write-Host "  AMF:              $amfDir"
Write-Host "  Video Codec SDK:  $($sdkDir.FullName)"
Write-Host "  oneVPL SDK:       $($oneVplSdk.FullName)"
Write-Host "  oneVPL runtime:   $($oneVplRuntime.FullName)"
Write-Host "  Props:            $propsPath"
