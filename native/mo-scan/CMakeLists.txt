add_executable(mo-scan
  src/main.cpp
  src/common.cpp
  src/library.cpp
  src/scan_clap.cpp
  src/scan_vst3.cpp
  src/scan_vst2.cpp
  ../third_party/vst3/pluginterfaces/base/funknown.cpp)
target_link_libraries(mo-scan PRIVATE clap mo-vst3-headers)
# $<0:> keeps multi-config generators (Visual Studio) from adding a Debug/Release subfolder.
set_target_properties(mo-scan PROPERTIES RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin/$<0:>)

if(MSVC)
  target_compile_options(mo-scan PRIVATE /W3)
  target_compile_definitions(mo-scan PRIVATE _CRT_SECURE_NO_WARNINGS UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
elseif(WIN32)
  target_link_options(mo-scan PRIVATE -municode)   # MinGW: use wmain
else()
  target_compile_options(mo-scan PRIVATE -Wall)
endif()

if(APPLE)
  target_link_libraries(mo-scan PRIVATE "-framework CoreFoundation")
elseif(UNIX)
  target_link_libraries(mo-scan PRIVATE ${CMAKE_DL_LIBS})
endif()
