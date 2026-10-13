#include "probe.h"

#include <clap/clap.h>

namespace {
bool CLAP_ABI init(const char* path) { return probe::entryInit(path); }
void CLAP_ABI deinit() { probe::entryDeinit(); }
const void* CLAP_ABI factory(const char* id) { return probe::entryFactory(id); }
} // namespace

extern "C" {
#ifdef __GNUC__
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wattributes"   // GCC warns about the attribute's position but still exports it
#endif
const CLAP_EXPORT struct clap_plugin_entry clap_entry = { CLAP_VERSION_INIT, init, deinit, factory };
#ifdef __GNUC__
#pragma GCC diagnostic pop
#endif
}
