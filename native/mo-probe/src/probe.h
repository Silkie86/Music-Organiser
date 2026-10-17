// The probe's entry points. entry.cpp exports them as clap_entry; clap-wrapper builds the
// VST3 and AU versions from the same functions.
#pragma once

namespace probe {

bool entryInit(const char* pluginPath);
void entryDeinit();
const void* entryFactory(const char* factoryId);

/// Where the plugin binary is, and which format the DAW loaded it as ("CLAP", "VST3" or "AU").
const char* pluginPath();
const char* wrappedFormat();

} // namespace probe
