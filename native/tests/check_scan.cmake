# Runs mo-scan on the probe and checks it found both plugins and their parameters.
#   cmake -DSCANNER=<mo-scan> -DPLUGIN=<probe> -DFORMAT=CLAP|VST3 -P check_scan.cmake

# On Linux a VST3's target file is inside its bundle (MO Probe.vst3/Contents/x86_64-linux/MO Probe.so):
# scan the bundle, as a DAW would.
if(FORMAT STREQUAL "VST3")
  string(REGEX REPLACE "(\\.vst3)[/\\\\].*$" "\\1" PLUGIN "${PLUGIN}")
endif()

execute_process(COMMAND "${SCANNER}" "${PLUGIN}" OUTPUT_VARIABLE out ERROR_VARIABLE err RESULT_VARIABLE rc)
message("${out}")
if(NOT rc EQUAL 0)
  message(FATAL_ERROR "mo-scan failed (${rc}): ${err}")
endif()

set(expect
  "\"format\":\"${FORMAT}\""
  "\"name\":\"MO Probe Instrument\""
  "\"name\":\"MO Probe Effect\""
  "\"vendor\":\"Music Organiser\""
  "\"kind\":\"instrument\""
  "\"kind\":\"effect\""
  "\"name\":\"MO Knob A\""
  "\"name\":\"MO Ramp\""
  "\"name\":\"MO Bypass\"")
if(FORMAT STREQUAL "CLAP")
  list(APPEND expect "\"id\":\"org.musicorganiser.probe.instrument\"" "\"id\":1007")
else()
  # The fixed VST3 class IDs from probe.cpp.
  list(APPEND expect "\"id\":\"B0DDF78C9D004BA9B2359D3E7B74D7F1\"" "\"id\":\"805ECA49563E43EA8119369F0ED6096D\"")
endif()
foreach(e IN LISTS expect)
  string(FIND "${out}" "${e}" at)
  if(at EQUAL -1)
    message(FATAL_ERROR "mo-scan output is missing ${e}")
  endif()
endforeach()
message(STATUS "mo-scan found both ${FORMAT} probes and their parameters")
