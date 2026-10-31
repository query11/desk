cmake_minimum_required(VERSION 3.25)

project(DeskTap VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# The DLL is loaded into explorer.exe and the Start menu host, which can't be
# assumed to have the VC++ runtime available: link the CRT statically.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

find_package(minhook CONFIG REQUIRED)

add_library(DeskTap SHARED
    src/blur.cpp
    src/clickthrough.cpp
    src/config.cpp
    src/control.cpp
    src/dllmain.cpp
    src/effects.cpp
    src/engine.cpp
    src/install.cpp
    src/log.cpp
    src/monitor.cpp
    src/paths.cpp
    src/regpatch.cpp
    src/rules.cpp
    src/status.cpp
    src/surface.cpp
    src/tap.cpp
    src/values.cpp
    src/watcher.cpp
    src/winutil.cpp
    DeskTap.def
)

target_precompile_headers(DeskTap PRIVATE src/pch.h)

target_compile_definitions(DeskTap PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _WIN32_WINNT=0x0A00
    WINVER=0x0A00
)

target_compile_options(DeskTap PRIVATE /W3 /permissive- /Zc:__cplusplus /utf-8 /EHsc /bigobj)

# C++/WinRT headers ship with the Windows SDK; the developer environment does
# not always put them on the include path.
if(DEFINED ENV{WindowsSdkDir} AND DEFINED ENV{WindowsSDKVersion})
    file(TO_CMAKE_PATH "$ENV{WindowsSdkDir}" _sdk_dir)
    string(REGEX REPLACE "[\\\\/]+$" "" _sdk_version "$ENV{WindowsSDKVersion}")
    target_include_directories(DeskTap SYSTEM PRIVATE
        "${_sdk_dir}/Include/${_sdk_version}/cppwinrt")
endif()

target_link_libraries(DeskTap PRIVATE
    minhook::minhook
    runtimeobject
    ole32
    oleaut32
    user32
    gdi32
    iphlpapi
    advapi32
    comctl32
    shlwapi
    dxguid
)

set_target_properties(DeskTap PROPERTIES OUTPUT_NAME "DeskTap")

# Unit tests for the decisions in src/core, which don't depend on Windows.
option(DESKTAP_BUILD_TESTS "Build DeskTapCoreTests" ON)
if(DESKTAP_BUILD_TESTS)
    add_executable(DeskTapCoreTests tests/core_tests.cpp)
    target_include_directories(DeskTapCoreTests PRIVATE src)
    if(MSVC)
        target_compile_options(DeskTapCoreTests PRIVATE /W3 /permissive- /utf-8 /EHsc)
    endif()
    enable_testing()
    add_test(NAME DeskTapCoreTests COMMAND DeskTapCoreTests)
endif()
