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

# Each knob's text at even steps, for matching one plugin's knobs to another's. The knob is put back after.
# (No "[" in what's expected: it would stop CMake splitting the list.)
scan(curve --only MoB2 --curve 4)
expect("${curve}" "{\"x\":0,\"value\":0,\"text\":\"0.00\"}" "{\"x\":0.75,\"value\":0.75,\"text\":\"0.75\"}"
       "\"value\":0.5,\"text\":\"0.50\",\"curve\"")

# A saved state handed over first: the knobs are read after it, and the state printed back.
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/fake-state.txt" "0.75")
scan(load --only MoB2 --load "${CMAKE_CURRENT_BINARY_DIR}/fake-state.txt")
expect("${load}" "\"loaded\":4,\"loadResult\":1" "\"name\":\"Mix\",\"units\":\"%\",\"value\":0.75" "\"state\":")
scan(program --only MoB2 --load "${CMAKE_CURRENT_BINARY_DIR}/fake-state.txt" --load-program)
expect("${program}" "\"name\":\"Mode\",\"units\":\"\",\"value\":0.75")

# Everything.
scan(all)
expect("${all}" "\"kind\":\"instrument\"" "\"kind\":\"effect\"" "\"name\":\"Mode\"" "\"version\":\"1200\"")
message(STATUS "mo-scan read the fake VST2 shell")
