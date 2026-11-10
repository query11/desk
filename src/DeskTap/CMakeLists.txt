cmake_minimum_required(VERSION 3.25)

project(DeskTap VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# The DLL is loaded into explorer.exe and the Start menu host, which can't be
# assumed to have the VC++ runtime available: link the CRT statically.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

find_package(minhook CONFIG REQUIRED)

add_library(DeskTap SHARED
    src/adaptive.cpp
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

    add_executable(DeskTapEffectsTests tests/effects_tests.cpp src/effects.cpp)
    target_include_directories(DeskTapEffectsTests PRIVATE src)
    target_precompile_headers(DeskTapEffectsTests PRIVATE src/pch.h)
    target_compile_options(DeskTapEffectsTests PRIVATE /W3 /permissive- /utf-8 /EHsc /bigobj)
    target_link_libraries(DeskTapEffectsTests PRIVATE runtimeobject ole32 oleaut32 coremessaging dxguid user32)
    if(DEFINED _sdk_dir AND DEFINED _sdk_version)
        target_include_directories(DeskTapEffectsTests SYSTEM PRIVATE
            "${_sdk_dir}/Include/${_sdk_version}/cppwinrt")
    endif()
    add_test(NAME DeskTapEffectsTests COMMAND DeskTapEffectsTests)
    add_executable(DeskTapConfigTests tests/config_tests.cpp src/config.cpp src/rules.cpp)
    target_include_directories(DeskTapConfigTests PRIVATE src)
    target_precompile_headers(DeskTapConfigTests PRIVATE src/pch.h)
    target_compile_definitions(DeskTapConfigTests PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_compile_options(DeskTapConfigTests PRIVATE /W3 /permissive- /utf-8 /EHsc /bigobj)
    target_link_libraries(DeskTapConfigTests PRIVATE runtimeobject ole32 user32)
    if(DEFINED _sdk_dir AND DEFINED _sdk_version)
        target_include_directories(DeskTapConfigTests SYSTEM PRIVATE "${_sdk_dir}/Include/${_sdk_version}/cppwinrt")
    endif()
    add_test(NAME DeskTapConfigTests COMMAND DeskTapConfigTests)
endif()
