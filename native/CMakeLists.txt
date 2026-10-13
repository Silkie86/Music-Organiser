# Native tools for Music Organiser: the plugin scanner (mo-scan) and the probe plugin (mo-probe).
#
#   cmake -S native -B native/build
#   cmake --build native/build --config Release
#
# Builds offline: the CLAP and VST3 headers it needs are in third_party (both MIT licensed).
# -DMO_PROBE_WRAPPERS=ON also builds the probe as VST3 (and AU on macOS) with clap-wrapper,
# which downloads clap-wrapper and the full VST3 SDK with git.
cmake_minimum_required(VERSION 3.21)
project(MusicOrganiserNative VERSION 1.0.0 LANGUAGES C CXX)

option(MO_PROBE_WRAPPERS "Also build the probe as VST3 (and AU on macOS) with clap-wrapper (needs git and internet)" OFF)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Everything may end up inside a plugin (a shared library), including clap-wrapper's static parts.
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if(APPLE)
  set(CMAKE_OSX_DEPLOYMENT_TARGET "11.0" CACHE STRING "Oldest macOS to support")
endif()
if(MSVC)
  # Self-contained binaries: no Visual C++ runtime to install alongside them.
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  add_compile_options(/utf-8)
endif()

# Named "clap" so clap-wrapper uses these headers instead of downloading its own.
add_library(clap INTERFACE)
target_include_directories(clap INTERFACE ${CMAKE_CURRENT_SOURCE_DIR}/third_party/clap/include)

add_library(mo-vst3-headers INTERFACE)
target_include_directories(mo-vst3-headers INTERFACE ${CMAKE_CURRENT_SOURCE_DIR}/third_party/vst3)

add_subdirectory(mo-scan)
add_subdirectory(mo-probe)

# Tests: host the probe like a DAW, and scan it with mo-scan.  ctest --test-dir native/build -C Release
include(CTest)
if(BUILD_TESTING)
  add_executable(mo-probe-test tests/probe_test.cpp mo-scan/src/library.cpp)
  target_include_directories(mo-probe-test PRIVATE mo-scan/src)
  target_link_libraries(mo-probe-test PRIVATE clap)
  if(APPLE)
    target_link_libraries(mo-probe-test PRIVATE "-framework CoreFoundation")
  elseif(UNIX)
    target_link_libraries(mo-probe-test PRIVATE ${CMAKE_DL_LIBS})
  endif()
  if(MSVC)
    target_compile_definitions(mo-probe-test PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX)
  endif()

  # On macOS a plugin is a bundle folder; elsewhere it's the file itself.
  if(APPLE)
    set(probe_clap "$<TARGET_BUNDLE_DIR:${MO_PROBE_CLAP_TARGET}>")
  else()
    set(probe_clap "$<TARGET_FILE:${MO_PROBE_CLAP_TARGET}>")
  endif()
  add_test(NAME probe-in-a-host COMMAND mo-probe-test "${probe_clap}" "${CMAKE_BINARY_DIR}/test-reports")
  add_test(NAME scan-clap-probe
    COMMAND ${CMAKE_COMMAND} -DSCANNER=$<TARGET_FILE:mo-scan> "-DPLUGIN=${probe_clap}" -DFORMAT=CLAP
            -P ${CMAKE_CURRENT_SOURCE_DIR}/tests/check_scan.cmake)
  if(MO_PROBE_VST3_TARGET)
    if(APPLE)
      set(probe_vst3 "$<TARGET_BUNDLE_DIR:${MO_PROBE_VST3_TARGET}>")
    else()
      set(probe_vst3 "$<TARGET_FILE:${MO_PROBE_VST3_TARGET}>")
    endif()
    add_test(NAME scan-vst3-probe
      COMMAND ${CMAKE_COMMAND} -DSCANNER=$<TARGET_FILE:mo-scan> "-DPLUGIN=${probe_vst3}" -DFORMAT=VST3
              -P ${CMAKE_CURRENT_SOURCE_DIR}/tests/check_scan.cmake)
  endif()
endif()
