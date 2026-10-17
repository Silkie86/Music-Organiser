# Runs mo-scan on the fake VST2 shell (fake_vst2.cpp) the three ways the app does.
#   cmake -DSCANNER=<mo-scan> -DPLUGIN=<fake vst2> -P check_vst2.cmake

function(scan out)
  execute_process(COMMAND "${SCANNER}" ${ARGN} "${PLUGIN}" OUTPUT_VARIABLE text ERROR_VARIABLE err RESULT_VARIABLE rc)
  message("mo-scan ${ARGN}:\n${text}")
  if(NOT rc EQUAL 0)
    message(FATAL_ERROR "mo-scan failed (${rc}): ${err}")
  endif()
  set(${out} "${text}" PARENT_SCOPE)
endfunction()

function(expect text)
  foreach(e IN LISTS ARGN)
    string(FIND "${text}" "${e}" at)
    if(at EQUAL -1)
      message(FATAL_ERROR "mo-scan output is missing ${e}")
    endif()
  endforeach()
endfunction()

function(expect_not text)
  foreach(e IN LISTS ARGN)
    string(FIND "${text}" "${e}" at)
    if(NOT at EQUAL -1)
      message(FATAL_ERROR "mo-scan output shouldn't have ${e}")
    endif()
  endforeach()
endfunction()

# The quick list the app makes of every plugin: the shell's plugins without opening each one.
scan(list --no-params)
expect("${list}" "\"format\":\"VST2\"" "\"id\":\"MoA1\"" "\"id\":\"MoB2\"" "\"name\":\"Fake Synth\""
       "\"name\":\"Fake Delay\"" "\"vendor\":\"Fake Vendor\"")
expect_not("${list}" "\"params\"")

# One plugin's parameters, when you look at it in the app.
scan(one --only MoB2)
expect("${one}" "\"id\":\"MoB2\"" "\"kind\":\"effect\"" "\"audioIn\":2" "\"name\":\"Cutoff\"" "\"units\":\"Hz\""
       "\"text\":\"0.25\"")
expect_not("${one}" "Fake Synth")

# Everything.
scan(all)
expect("${all}" "\"kind\":\"instrument\"" "\"kind\":\"effect\"" "\"name\":\"Mode\"" "\"version\":\"1200\"")
# A plugin that sets a locale with a decimal comma as it loads still gets its numbers written with a point, so the
# line stays JSON. (Where the system has no such locale, this reads as above.)
set(ENV{FAKE_VST2_LOCALE} "de_DE.UTF-8")
scan(comma --only MoB2)
unset(ENV{FAKE_VST2_LOCALE})
expect("${comma}" "\"value\":0.25")
expect_not("${comma}" "\"value\":0,25")

message(STATUS "mo-scan read the fake VST2 shell")
