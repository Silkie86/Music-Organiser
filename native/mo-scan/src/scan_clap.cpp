// CLAP: load the plugin, list every plugin in its factory, and read ports, parameters and state.
#include "json.h"
#include "module.h"

#include <clap/clap.h>

#include <cstring>
#include <vector>

namespace mo {
namespace {

const clap_host_thread_check_t threadCheck{
    [](const clap_host_t*) { return true; },    // is_main_thread: we only use one thread
    [](const clap_host_t*) { return false; },   // is_audio_thread
};

const clap_host_log_t hostLog{
    [](const clap_host_t*, clap_log_severity, const char* msg) { std::fprintf(stderr, "[plugin] %s\n", msg ? msg : ""); },
};

const clap_host_params_t hostParams{
    [](const clap_host_t*, clap_param_rescan_flags) {},
    [](const clap_host_t*, clap_id, clap_param_clear_flags) {},
    [](const clap_host_t*) {},
};

const clap_host_state_t hostState{ [](const clap_host_t*) {} };

const void* getExtension(const clap_host_t*, const char* id)
{
    if (!std::strcmp(id, CLAP_EXT_THREAD_CHECK)) return &threadCheck;
    if (!std::strcmp(id, CLAP_EXT_LOG)) return &hostLog;
    if (!std::strcmp(id, CLAP_EXT_PARAMS)) return &hostParams;
    if (!std::strcmp(id, CLAP_EXT_STATE)) return &hostState;
    return nullptr;
}

clap_host_t makeHost()
{
    clap_host_t h{};
    h.clap_version = CLAP_VERSION;
    h.name = "Music Organiser scanner";
    h.vendor = "Music Organiser";
    h.url = "";
    h.version = "1.0";
    h.get_extension = getExtension;
    h.request_restart = [](const clap_host_t*) {};
    h.request_process = [](const clap_host_t*) {};
    h.request_callback = [](const clap_host_t*) {};
    return h;
}

std::string kindOf(const std::vector<std::string>& features)
{
    for (auto& f : features)
    {
        if (f == CLAP_PLUGIN_FEATURE_INSTRUMENT) return "instrument";
        if (f == CLAP_PLUGIN_FEATURE_NOTE_EFFECT) return "note-effect";
        if (f == CLAP_PLUGIN_FEATURE_ANALYZER) return "analyzer";
    }
    return "effect";
}

void writePorts(Json& j, const clap_plugin_t* p)
{
    if (auto ap = static_cast<const clap_plugin_audio_ports_t*>(p->get_extension(p, CLAP_EXT_AUDIO_PORTS)))
    {
        for (bool input : { true, false })
        {
            int channels = 0;
            for (uint32_t i = 0, n = ap->count(p, input); i < n; i++)
            {
                clap_audio_port_info_t info{};
                if (ap->get(p, i, input, &info)) channels += static_cast<int>(info.channel_count);
            }
            j.integer(input ? "audioIn" : "audioOut", channels);
        }
    }
    if (auto np = static_cast<const clap_plugin_note_ports_t*>(p->get_extension(p, CLAP_EXT_NOTE_PORTS)))
    {
        j.integer("noteIn", np->count(p, true));
        j.integer("noteOut", np->count(p, false));
    }
}

void writeParams(Json& j, const clap_plugin_t* p)
{
    auto params = static_cast<const clap_plugin_params_t*>(p->get_extension(p, CLAP_EXT_PARAMS));
    j.beginArray("params");
    if (params)
    {
        for (uint32_t i = 0, n = params->count(p); i < n; i++)
        {
            clap_param_info_t info{};
            if (!params->get_info(p, i, &info)) continue;
            j.item()
                .integer("id", info.id)
                .str("name", cleanUtf8(info.name, sizeof info.name))
                .str("module", cleanUtf8(info.module, sizeof info.module))
                .num("min", info.min_value)
                .num("max", info.max_value)
                .num("default", info.default_value);
            double value = info.default_value;
            if (params->get_value(p, info.id, &value)) j.num("value", value);
            char text[256] = {};
            if (params->value_to_text && params->value_to_text(p, info.id, value, text, sizeof text))
                j.str("text", cleanUtf8(text, sizeof text));
            j.boolean("stepped", info.flags & CLAP_PARAM_IS_STEPPED)
                .boolean("automatable", info.flags & CLAP_PARAM_IS_AUTOMATABLE)
                .boolean("hidden", info.flags & CLAP_PARAM_IS_HIDDEN)
                .boolean("readOnly", info.flags & CLAP_PARAM_IS_READONLY)
                .boolean("bypass", info.flags & CLAP_PARAM_IS_BYPASS)
                .end();
        }
    }
    j.endArray();
}

void writeState(Json& j, const clap_plugin_t* p)
{
    auto state = static_cast<const clap_plugin_state_t*>(p->get_extension(p, CLAP_EXT_STATE));
    if (!state) return;
    std::vector<uint8_t> bytes;
    clap_ostream_t out{};
    out.ctx = &bytes;
    out.write = [](const clap_ostream_t* s, const void* buf, uint64_t size) -> int64_t {
        auto v = static_cast<std::vector<uint8_t>*>(s->ctx);
        auto b = static_cast<const uint8_t*>(buf);
        v->insert(v->end(), b, b + size);
        return static_cast<int64_t>(size);
    };
    if (state->save(p, &out)) j.str("state", base64(bytes));
}

} // namespace

bool scanClap(const std::filesystem::path& path, const Options& opt, std::string& error)
{
    Library lib;
    if (!lib.open(path, error)) return false;
    auto entry = static_cast<const clap_plugin_entry_t*>(lib.symbol("clap_entry"));
    if (!entry) { error = "no clap_entry"; return false; }
    if (!clap_version_is_compatible(entry->clap_version)) { error = "unsupported CLAP version"; return false; }
    if (!entry->init(path.u8string().c_str())) { error = "clap_entry init failed"; return false; }

    auto factory = static_cast<const clap_plugin_factory_t*>(entry->get_factory(CLAP_PLUGIN_FACTORY_ID));
    if (!factory) { error = "no plugin factory"; entry->deinit(); return false; }

    auto host = makeHost();
    for (uint32_t i = 0, n = factory->get_plugin_count(factory); i < n; i++)
    {
        auto d = factory->get_plugin_descriptor(factory, i);
        if (!d) continue;
        std::vector<std::string> features;
        for (auto f = d->features; f && *f; f++) features.emplace_back(cleanUtf8(*f, 256));

        Json j;
        j.begin()
            .str("format", "CLAP")
            .str("path", path.u8string())
            .str("id", cleanUtf8(d->id, 1024))
            .str("name", cleanUtf8(d->name, 1024))
            .str("vendor", cleanUtf8(d->vendor, 1024))
            .str("version", cleanUtf8(d->version, 1024))
            .str("kind", kindOf(features))
            .strings("features", features);

        if (opt.params || opt.state)
        {
            auto p = factory->create_plugin(factory, &host, d->id);
            if (p && p->init(p))
            {
                writePorts(j, p);
                if (opt.params) writeParams(j, p);
                if (opt.state) writeState(j, p);
                p->destroy(p);
            }
            else
            {
                if (p) p->destroy(p);
                j.str("error", "couldn't create the plugin");
            }
        }
        emit(j.end());
    }
    entry->deinit();
    return true;
}

} // namespace mo
