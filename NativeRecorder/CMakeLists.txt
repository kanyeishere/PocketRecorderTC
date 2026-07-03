cmake_minimum_required(VERSION 3.24)

project(NativeRecorder LANGUAGES CXX)

add_library(NativeRecorder SHARED
    src/native_recorder.cpp
    src/native_recorder_exports.cpp
)

target_compile_features(NativeRecorder PRIVATE cxx_std_20)
target_include_directories(NativeRecorder PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

option(POCKETRECORDER_ENABLE_NVENC "Build the NVIDIA NVENC/D3D11 encoder backend." OFF)
set(POCKETRECORDER_VIDEO_CODEC_SDK_DIR "" CACHE PATH "Path to NVIDIA Video Codec SDK root.")

if (POCKETRECORDER_ENABLE_NVENC)
    if (NOT POCKETRECORDER_VIDEO_CODEC_SDK_DIR)
        message(FATAL_ERROR "POCKETRECORDER_VIDEO_CODEC_SDK_DIR is required when POCKETRECORDER_ENABLE_NVENC=ON.")
    endif()

    target_compile_definitions(NativeRecorder PRIVATE PR_ENABLE_NVENC=1)
    target_include_directories(NativeRecorder PRIVATE
        ${POCKETRECORDER_VIDEO_CODEC_SDK_DIR}/Interface
        ${POCKETRECORDER_VIDEO_CODEC_SDK_DIR}/Samples/NvCodec
    )
endif()

target_compile_definitions(NativeRecorder PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

if (MSVC)
    target_compile_options(NativeRecorder PRIVATE /W4 /permissive- /EHsc)
endif()
