# MO Probe: one CLAP holding an instrument and an effect that report what the DAW does with them.
find_package(Threads REQUIRED)

add_library(mo-probe-impl STATIC src/probe.cpp src/log.cpp)
target_link_libraries(mo-probe-impl PUBLIC clap Threads::Threads)
if(MSVC)
  target_compile_definitions(mo-probe-impl PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX)
else()
  target_compile_options(mo-probe-impl PRIVATE -Wall)
endif()

# $<0:> keeps multi-config generators (Visual Studio) from adding a Debug/Release subfolder.
set(MO_PLUGIN_DIR ${CMAKE_BINARY_DIR}/plugins)

if(MO_PROBE_WRAPPERS)
  # clap-wrapper turns the CLAP into a VST3 (and an AU on macOS). It downloads the VST3 SDK
  # (MIT licensed) and, on macOS, Apple's AudioUnitSDK.
  include(FetchContent)
  set(CLAP_WRAPPER_DOWNLOAD_DEPENDENCIES TRUE CACHE BOOL "" FORCE)
  FetchContent_Declare(clap-wrapper
    GIT_REPOSITORY https://github.com/free-audio/clap-wrapper.git
    GIT_TAG v0.16.0
    GIT_SHALLOW TRUE)
  FetchContent_MakeAvailable(clap-wrapper)
  target_link_libraries(mo-probe-impl PUBLIC clap-wrapper-extensions)

  set(MO_PROBE_FORMATS CLAP VST3)
  if(APPLE)
    list(APPEND MO_PROBE_FORMATS AUV2)
  endif()
  make_clapfirst_plugins(
    TARGET_NAME mo-probe
    IMPL_TARGET mo-probe-impl
    OUTPUT_NAME "MO Probe"
    ENTRY_SOURCE src/entry.cpp
    BUNDLE_IDENTIFIER "org.musicorganiser.probe"
    BUNDLE_VERSION ${PROJECT_VERSION}
    COPY_AFTER_BUILD FALSE
    PLUGIN_FORMATS ${MO_PROBE_FORMATS}
    ASSET_OUTPUT_DIRECTORY ${MO_PLUGIN_DIR})
  set(MO_PROBE_CLAP_TARGET mo-probe_clap PARENT_SCOPE)
  set(MO_PROBE_VST3_TARGET mo-probe_vst3 PARENT_SCOPE)
else()
  add_library(mo-probe MODULE src/entry.cpp)
  target_link_libraries(mo-probe PRIVATE mo-probe-impl)
  set_target_properties(mo-probe PROPERTIES
    OUTPUT_NAME "MO Probe"
    PREFIX ""
    LIBRARY_OUTPUT_DIRECTORY ${MO_PLUGIN_DIR}/$<0:>)
  if(APPLE)
    set_target_properties(mo-probe PROPERTIES
      BUNDLE TRUE
      BUNDLE_EXTENSION clap
      MACOSX_BUNDLE_GUI_IDENTIFIER org.musicorganiser.probe.clap
      MACOSX_BUNDLE_BUNDLE_NAME "MO Probe"
      MACOSX_BUNDLE_BUNDLE_VERSION ${PROJECT_VERSION}
      MACOSX_BUNDLE_SHORT_VERSION_STRING ${PROJECT_VERSION})
  else()
    set_target_properties(mo-probe PROPERTIES SUFFIX ".clap")
  endif()
  set(MO_PROBE_CLAP_TARGET mo-probe PARENT_SCOPE)
endif()
