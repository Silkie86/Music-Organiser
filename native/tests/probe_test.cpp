// Hosts the probe like a DAW would and checks what it reports: the track, transport, a note at a
// known beat, a parameter change, and a save and load of its state.
//
//   mo-probe-test <path to "MO Probe.clap"> <empty folder for the reports>
#include "library.h"

#include <clap/clap.h>

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

namespace {

int failures = 0;

#define CHECK(cond, what)                                                         \
    do                                                                            \
    {                                                                             \
        if (!(cond))                                                              \
        {                                                                         \
            std::fprintf(stderr, "FAIL: %s (line %d)\n", what, __LINE__);         \
            failures++;                                                           \
        }                                                                         \
        else std::printf("ok: %s\n", what);                                       \
    } while (0)

// ---- A tiny host ----

bool trackInfo(const clap_host_t*, clap_track_info_t* info)
{
    *info = clap_track_info_t{};
    info->flags = CLAP_TRACK_INFO_HAS_TRACK_NAME | CLAP_TRACK_INFO_HAS_TRACK_COLOR;
    std::snprintf(info->name, sizeof info->name, "%s", "Bass");
    info->color = { 0xFF, 0x40, 0x80, 0xC0 };   // alpha, red, green, blue
    return true;
}

const clap_host_track_info_t kTrackInfo = { trackInfo };

const void* hostExtension(const clap_host_t*, const char* id)
{
    if (!std::strcmp(id, CLAP_EXT_TRACK_INFO)) return &kTrackInfo;
    return nullptr;
}

clap_host_t makeHost()
{
    clap_host_t h{};
    h.clap_version = CLAP_VERSION;
    h.name = "Probe Test Host";
    h.vendor = "Music Organiser";
    h.url = "";
    h.version = "9.9";
    h.get_extension = hostExtension;
    h.request_restart = [](const clap_host_t*) {};
    h.request_process = [](const clap_host_t*) {};
    h.request_callback = [](const clap_host_t*) {};
    return h;
}

struct InEvents
{
    clap_input_events_t api{};
    std::vector<std::vector<uint8_t>> items;

    InEvents()
    {
        api.ctx = this;
        api.size = [](const clap_input_events_t* e) { return static_cast<uint32_t>(static_cast<InEvents*>(e->ctx)->items.size()); };
        api.get = [](const clap_input_events_t* e, uint32_t i) {
            auto self = static_cast<InEvents*>(e->ctx);
            return i < self->items.size() ? reinterpret_cast<const clap_event_header_t*>(self->items[i].data()) : nullptr;
        };
    }
    template <typename T> void add(const T& ev)
    {
        auto p = reinterpret_cast<const uint8_t*>(&ev);
        items.emplace_back(p, p + sizeof ev);
    }
};

struct OutEvents
{
    clap_output_events_t api{};
    int count = 0;
    OutEvents()
    {
        api.ctx = this;
        api.try_push = [](const clap_output_events_t* e, const clap_event_header_t*) {
            static_cast<OutEvents*>(e->ctx)->count++;
            return true;
        };
    }
};

clap_event_header_t header(uint32_t time, uint16_t type, uint32_t size)
{
    clap_event_header_t h{};
    h.size = size;
    h.time = time;
    h.space_id = CLAP_CORE_EVENT_SPACE_ID;
    h.type = type;
    return h;
}

clap_event_transport_t transportAt(double beats, double tempo)
{
    clap_event_transport_t t{};
    t.header = header(0, CLAP_EVENT_TRANSPORT, sizeof t);
    t.flags = CLAP_TRANSPORT_HAS_TEMPO | CLAP_TRANSPORT_HAS_BEATS_TIMELINE | CLAP_TRANSPORT_HAS_SECONDS_TIMELINE
              | CLAP_TRANSPORT_HAS_TIME_SIGNATURE | CLAP_TRANSPORT_IS_PLAYING;
    t.song_pos_beats = static_cast<clap_beattime>(std::llround(beats * CLAP_BEATTIME_FACTOR));
    t.song_pos_seconds = static_cast<clap_sectime>(std::llround(beats * 60.0 / tempo * CLAP_SECTIME_FACTOR));
    t.tempo = tempo;
    int bar = static_cast<int>(beats / 4);
    t.bar_start = static_cast<clap_beattime>(bar * 4) * CLAP_BEATTIME_FACTOR;
    t.bar_number = bar;
    t.tsig_num = 4;
    t.tsig_denom = 4;
    return t;
}

struct Stream
{
    std::vector<uint8_t> data;
    size_t pos = 0;
    clap_ostream_t out{};
    clap_istream_t in{};
    Stream()
    {
        out.ctx = this;
        out.write = [](const clap_ostream_t* s, const void* buf, uint64_t size) -> int64_t {
            auto self = static_cast<Stream*>(s->ctx);
            auto b = static_cast<const uint8_t*>(buf);
            self->data.insert(self->data.end(), b, b + size);
            return static_cast<int64_t>(size);
        };
        in.ctx = this;
        in.read = [](const clap_istream_t* s, void* buf, uint64_t size) -> int64_t {
            auto self = static_cast<Stream*>(s->ctx);
            size_t n = std::min<size_t>(static_cast<size_t>(size), self->data.size() - self->pos);
            std::memcpy(buf, self->data.data() + self->pos, n);
            self->pos += n;
            return static_cast<int64_t>(n);
        };
    }
};

std::filesystem::path reportFolder(const std::filesystem::path& base)
{
#if defined(_WIN32)
    return base / "MusicOrganiser" / "probe";
#elif defined(__APPLE__)
    return base / "Library" / "Application Support" / "MusicOrganiser" / "probe";
#else
    return base / ".local" / "share" / "MusicOrganiser" / "probe";
#endif
}

/// The number after "key": in a JSON line, or NAN.
double number(const std::string& line, const char* key)
{
    auto k = std::string("\"") + key + "\":";
    auto at = line.find(k);
    return at == std::string::npos ? NAN : std::strtod(line.c_str() + at + k.size(), nullptr);
}

const std::string* find(const std::vector<std::string>& lines, const char* event, const char* also = nullptr)
{
    auto e = std::string("\"event\":\"") + event + "\"";
    for (auto& l : lines)
        if (l.find(e) != std::string::npos && (!also || l.find(also) != std::string::npos)) return &l;
    return nullptr;
}

} // namespace

int main(int argc, char** argv)
{
    if (argc < 3)
    {
        std::fprintf(stderr, "usage: mo-probe-test <MO Probe.clap> <report folder>\n");
        return 64;
    }
    std::filesystem::path plugin = argv[1], base = argv[2];
    std::filesystem::remove_all(base);
    std::filesystem::create_directories(base);

    // Point the probe's report folder at the test folder.
#if defined(_WIN32)
    _wputenv_s(L"LOCALAPPDATA", base.wstring().c_str());
#else
    setenv("HOME", base.string().c_str(), 1);
    unsetenv("XDG_DATA_HOME");
#endif

    mo::Library lib;
    std::string error;
    if (!lib.open(plugin, error)) { std::fprintf(stderr, "couldn't load %s: %s\n", argv[1], error.c_str()); return 1; }
    auto entry = static_cast<const clap_plugin_entry_t*>(lib.symbol("clap_entry"));
    CHECK(entry != nullptr, "the plugin exports clap_entry");
    if (!entry) return 1;
    CHECK(entry->init(plugin.u8string().c_str()), "clap_entry init");
    auto factory = static_cast<const clap_plugin_factory_t*>(entry->get_factory(CLAP_PLUGIN_FACTORY_ID));
    CHECK(factory && factory->get_plugin_count(factory) == 2, "two plugins in the factory");

    auto host = makeHost();
    auto p = factory->create_plugin(factory, &host, "org.musicorganiser.probe.instrument");
    CHECK(p && p->init(p), "create the instrument");
    auto params = static_cast<const clap_plugin_params_t*>(p->get_extension(p, CLAP_EXT_PARAMS));
    auto state = static_cast<const clap_plugin_state_t*>(p->get_extension(p, CLAP_EXT_STATE));
    CHECK(params && params->count(p) == 8, "eight parameters");
    CHECK(state != nullptr, "state extension");

    const double rate = 48000, tempo = 120;
    const uint32_t frames = 480;
    CHECK(p->activate(p, rate, 1, frames), "activate");
    CHECK(p->start_processing(p), "start processing");

    std::vector<float> left(frames), right(frames);
    float* outs[2] = { left.data(), right.data() };
    clap_audio_buffer_t out{};
    out.data32 = outs;
    out.channel_count = 2;

    float loudest = 0;
    for (int block = 0; block < 6; block++)
    {
        InEvents in;
        OutEvents outEvents;
        auto transport = transportAt(block * frames / rate * tempo / 60.0, tempo);
        if (block == 1)
        {
            clap_event_param_value_t pv{};
            pv.header = header(0, CLAP_EVENT_PARAM_VALUE, sizeof pv);
            pv.param_id = 1007;
            pv.value = 75;
            pv.note_id = -1;
            pv.port_index = -1;
            pv.channel = -1;
            pv.key = -1;
            in.add(pv);
            clap_event_note_t on{};
            on.header = header(240, CLAP_EVENT_NOTE_ON, sizeof on);
            on.note_id = 7;
            on.port_index = 0;
            on.channel = 0;
            on.key = 60;
            on.velocity = 0.8;
            in.add(on);
        }
        if (block == 3)
        {
            clap_event_note_t off{};
            off.header = header(0, CLAP_EVENT_NOTE_OFF, sizeof off);
            off.note_id = 7;
            off.port_index = 0;
            off.channel = 0;
            off.key = 60;
            in.add(off);
        }
        clap_process_t proc{};
        proc.steady_time = block * frames;
        proc.frames_count = frames;
        proc.transport = &transport;
        proc.audio_outputs = &out;
        proc.audio_outputs_count = 1;
        proc.in_events = &in.api;
        proc.out_events = &outEvents.api;
        p->process(p, &proc);
        if (block == 2)
            for (float x : left) loudest = std::max(loudest, std::fabs(x));
    }
    CHECK(loudest > 0.01f, "the instrument plays the note");

    double value = 0;
    CHECK(params->get_value(p, 1007, &value) && value == 75, "the parameter change sticks");

    Stream saved;
    CHECK(state->save(p, &saved.out), "save state");
    std::string text(saved.data.begin(), saved.data.end());
    CHECK(text.rfind("MOPROBE1", 0) == 0, "state starts with the marker");
    CHECK(text.find("\"1007\":75") != std::string::npos, "state holds the parameter");

    p->stop_processing(p);
    p->deactivate(p);
    p->destroy(p);

    auto p2 = factory->create_plugin(factory, &host, "org.musicorganiser.probe.instrument");
    CHECK(p2 && p2->init(p2), "create a second instrument");
    auto params2 = static_cast<const clap_plugin_params_t*>(p2->get_extension(p2, CLAP_EXT_PARAMS));
    auto state2 = static_cast<const clap_plugin_state_t*>(p2->get_extension(p2, CLAP_EXT_STATE));
    CHECK(state2->load(p2, &saved.in), "load the saved state");
    value = 0;
    CHECK(params2->get_value(p2, 1007, &value) && value == 75, "the loaded state restores the parameter");
    p2->destroy(p2);
    entry->deinit();

    // Read the reports (the plugins have finished writing them when destroy returns).
    std::vector<std::string> lines;
    std::error_code ec;
    for (auto& f : std::filesystem::directory_iterator(reportFolder(base), ec))
    {
        std::ifstream file(f.path());
        for (std::string line; std::getline(file, line);) lines.push_back(line);
    }
    CHECK(!lines.empty(), "the probe wrote a report");
    CHECK(find(lines, "create", "\"host\":\"Probe Test Host\"") != nullptr, "report names the host");
    CHECK(find(lines, "track", "\"name\":\"Bass\"") != nullptr, "report names the track");
    CHECK(find(lines, "track", "\"color\":\"#4080C0\"") != nullptr, "report has the track colour");
    CHECK(find(lines, "activate", "\"sampleRate\":48000") != nullptr, "report has the sample rate");
    CHECK(find(lines, "play", "\"tempo\":120") != nullptr, "report has play and the tempo");
    auto note = find(lines, "note-on", "\"key\":60");
    CHECK(note != nullptr, "report has the note");
    // Block 1 starts at 0.02 beats; 240 samples later at 120 bpm is 0.01 beats more.
    CHECK(note && std::fabs(number(*note, "beats") - 0.03) < 1e-6, "the note's position in beats is exact");
    CHECK(find(lines, "note-off", "\"key\":60") != nullptr, "report has the note off");
    CHECK(find(lines, "param", "\"id\":1007") != nullptr, "report has the parameter change");
    CHECK(find(lines, "save", "\"save\":1") != nullptr, "report has the save");
    auto load = find(lines, "load", "\"marker\":true");
    CHECK(load != nullptr, "report has the load");
    CHECK(load && load->find("\"params\":8") != std::string::npos, "the load restored all eight parameters");

    if (failures) std::fprintf(stderr, "%d check(s) failed\n", failures);
    else std::printf("all probe checks passed\n");
    return failures ? 1 : 0;
}
