param(
    [string] $FfmpegUrl = $env:FFMPEG_URL,
    [string] $FfmpegRootOverride = $env:NATIVE_RECORDER_FFMPEG_ROOT,
    [string] $UseMinimalFfmpeg = $env:USE_MINIMAL_FFMPEG,
    [string] $VideoCodecSdkUrl = $env:VIDEO_CODEC_SDK_URL,
    [string] $AmfRepo = $env:AMF_REPO,
    [string] $AmfRef = $env:AMF_REF,
    [string] $VideoCodecSdkDir = $env:VideoCodecSdkDir
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$depsRoot = Join-Path $repoRoot "lib\native"
$ffmpegRoot = Join-Path $depsRoot "ffmpeg-btbn-lgpl-shared"
$minimalFfmpegRoot = Join-Path $depsRoot "ffmpeg-minimal-lgpl-shared"
$amfRoot = Join-Path $depsRoot "AMF"
$videoCodecSdkRoot = Join-Path $depsRoot "VideoCodecSDK"
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

New-Item -ItemType Directory -Force -Path $depsRoot | Out-Null

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

    return Get-ChildItem $root -Directory -Recurse |
        Where-Object {
            (Test-Path (Join-Path $_.FullName "Interface\nvEncodeAPI.h")) -and
            (Test-Path (Join-Path $_.FullName "Samples\NvCodec\NvEncoder\NvEncoderD3D11.h")) -and
            (Test-Path (Join-Path $_.FullName "Lib\win\x64\nvencodeapi.lib"))
        } |
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

$sdkDir = $null
if (-not [string]::IsNullOrWhiteSpace($VideoCodecSdkDir) -and (Test-Path $VideoCodecSdkDir)) {
    $sdkDir = Find-VideoCodecSdkRoot $VideoCodecSdkDir
}
if ($null -eq $sdkDir) {
    $sdkDir = Find-VideoCodecSdkRoot $videoCodecSdkRoot
}
if ($null -eq $sdkDir) {
    if ([string]::IsNullOrWhiteSpace($VideoCodecSdkUrl)) {
        throw "VIDEO_CODEC_SDK_URL is required to build NativeRecorder in CI. Point it at a NVIDIA Video Codec SDK zip, for example a private release asset or repository variable."
    }

    $sdkZip = Join-Path $depsRoot "Video_Codec_SDK.zip"
    Write-Host "Downloading NVIDIA Video Codec SDK."
    Invoke-WebRequest -Uri $VideoCodecSdkUrl -OutFile $sdkZip -UseBasicParsing
    New-Item -ItemType Directory -Force -Path $videoCodecSdkRoot | Out-Null
    Expand-Archive -Path $sdkZip -DestinationPath $videoCodecSdkRoot -Force
    $sdkDir = Find-VideoCodecSdkRoot $videoCodecSdkRoot
}
if ($null -eq $sdkDir) {
    throw "NVIDIA Video Codec SDK was prepared, but nvEncodeAPI.h/NvEncoderD3D11.h/nvencodeapi.lib were not found."
}

Set-CiEnv "NATIVE_RECORDER_FFMPEG_ROOT" $ffmpegDir.FullName
Set-CiEnv "NATIVE_RECORDER_AMF_DIR" $amfDir
Set-CiEnv "NATIVE_RECORDER_VIDEO_CODEC_SDK_DIR" $sdkDir.FullName

$props = @"
<Project>
  <PropertyGroup>
    <FfmpegRoot>$($ffmpegDir.FullName)</FfmpegRoot>
    <NativeRecorderFfmpegRoot>$($ffmpegDir.FullName)</NativeRecorderFfmpegRoot>
    <AmfSdkDir>$amfDir</AmfSdkDir>
    <VideoCodecSdkDir>$($sdkDir.FullName)</VideoCodecSdkDir>
  </PropertyGroup>
</Project>
"@
$props | Set-Content -Path $propsPath -Encoding utf8

Write-Host "NativeRecorder dependency roots:"
Write-Host "  FFmpeg:           $($ffmpegDir.FullName)"
Write-Host "  AMF:              $amfDir"
Write-Host "  Video Codec SDK:  $($sdkDir.FullName)"
Write-Host "  Props:            $propsPath"
