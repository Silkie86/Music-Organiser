// MO Probe: a test plugin that reports what a DAW does with it, so Music Organiser can learn each
// DAW's formats exactly instead of by guesswork.
//
// - Its saved state is readable text that starts with "MOPROBE1", so searching a project file for
//   that marker shows where and how the DAW stores plugin state.
// - Its eight parameters have distinctive names, ranges and IDs (1001-1008), so automation and
//   parameter IDs are easy to spot in project files.
// - It writes a live report (see log.h): the host's name and version, the track it's on, transport,
//   every note and parameter change with its exact position in beats, and every save and load.
// - While it runs it keeps a status file up to date and takes commands from files (see live.h), so
//   the app, Bitwig's script and Claude can ask it things, move its knobs, label it and make it beep.
//
// Two plugins share this code: an instrument (plays a quiet sine per note) and an effect (gain).
#include "probe.h"
#include "live.h"
#include "log.h"

#include <clap/clap.h>

#include <algorithm>
#include <cctype>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <locale>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <vector>

#ifdef _WIN32
#include <process.h>
#else
#include <unistd.h>
#endif

#if __has_include(<clapwrapper/vst3.h>)
#include <clapwrapper/auv2.h>
#include <clapwrapper/vst3.h>
#define MO_PROBE_WRAPPED 1
#endif

namespace probe {
namespace {

constexpr const char* kVersion = "1.0.0";
constexpr double kTwoPi = 6.283185307179586;
constexpr const char* kMarker = "MOPROBE1";
constexpr const char* kInstrumentId = "org.musicorganiser.probe.instrument";
constexpr const char* kEffectId = "org.musicorganiser.probe.effect";

// ---- Parameters ----

const char* const kChoices[] = { "Alpha", "Bravo", "Charlie", "Delta" };

struct ParamDef
{
    clap_id id;
    const char* name;
    double min, max, def;
    uint32_t flags;
    const char* unit;
};

constexpr uint32_t kAuto = CLAP_PARAM_IS_AUTOMATABLE;
const ParamDef kParams[] = {
    { 1001, "MO Knob A", 0, 1, 0.25, kAuto, "" },
    { 1002, "MO Gain", -60, 12, 0, kAuto | CLAP_PARAM_IS_MODULATABLE, "dB" },
    { 1003, "MO Frequency", 20, 20000, 440, kAuto, "Hz" },
    { 1004, "MO Steps", 0, 7, 3, kAuto | CLAP_PARAM_IS_STEPPED, "" },
    { 1005, "MO Switch", 0, 1, 0, kAuto | CLAP_PARAM_IS_STEPPED, "" },
    { 1006, "MO Choice", 0, 3, 1, kAuto | CLAP_PARAM_IS_STEPPED | CLAP_PARAM_IS_ENUM, "" },
    { 1007, "MO Ramp", 0, 100, 50, kAuto | CLAP_PARAM_IS_MODULATABLE, "%" },
    { 1008, "MO Bypass", 0, 1, 0, kAuto | CLAP_PARAM_IS_STEPPED | CLAP_PARAM_IS_BYPASS, "" },
};
constexpr int kParamCount = sizeof kParams / sizeof kParams[0];
constexpr int kGain = 1, kBypass = 7;

int paramIndex(clap_id id)
{
    for (int i = 0; i < kParamCount; i++)
        if (kParams[i].id == id) return i;
    return -1;
}

void formatValue(int i, double v, char* out, size_t size)
{
    switch (kParams[i].id)
    {
    case 1004: std::snprintf(out, size, "%d", static_cast<int>(std::lround(v))); break;
    case 1005:
    case 1008: std::snprintf(out, size, "%s", v >= 0.5 ? "On" : "Off"); break;
    case 1006: std::snprintf(out, size, "%s", kChoices[std::clamp(static_cast<int>(std::lround(v)), 0, 3)]); break;
    case 1002: std::snprintf(out, size, "%.1f dB", v); break;
    case 1003: std::snprintf(out, size, v < 1000 ? "%.1f Hz" : "%.0f Hz", v); break;
    case 1007: std::snprintf(out, size, "%.1f %%", v); break;
    default: std::snprintf(out, size, "%.3f", v); break;
    }
}

bool parseValue(int i, const char* text, double& out)
{
    if (kParams[i].id == 1005 || kParams[i].id == 1008)
    {
        if (!std::strncmp(text, "On", 2) || !std::strncmp(text, "on", 2)) { out = 1; return true; }
        if (!std::strncmp(text, "Off", 3) || !std::strncmp(text, "off", 3)) { out = 0; return true; }
    }
    if (kParams[i].id == 1006)
        for (int c = 0; c < 4; c++)
            if (!std::strcmp(text, kChoices[c])) { out = c; return true; }
    char* end = nullptr;
    double v = std::strtod(text, &end);
    if (end == text) return false;
    out = std::clamp(v, kParams[i].min, kParams[i].max);
    return true;
}

// ---- Descriptors ----

const char* const kInstrumentFeatures[] = { CLAP_PLUGIN_FEATURE_INSTRUMENT, CLAP_PLUGIN_FEATURE_SYNTHESIZER,
                                            CLAP_PLUGIN_FEATURE_STEREO, nullptr };
const char* const kEffectFeatures[] = { CLAP_PLUGIN_FEATURE_AUDIO_EFFECT, CLAP_PLUGIN_FEATURE_UTILITY,
                                        CLAP_PLUGIN_FEATURE_STEREO, nullptr };

const clap_plugin_descriptor_t kDescriptors[] = {
    { CLAP_VERSION_INIT, kInstrumentId, "MO Probe Instrument", "Music Organiser", "", "", "", kVersion,
      "Reports what the DAW sends it to the Music Organiser app", kInstrumentFeatures },
    { CLAP_VERSION_INIT, kEffectId, "MO Probe Effect", "Music Organiser", "", "", "", kVersion,
      "Reports what the DAW sends it to the Music Organiser app", kEffectFeatures },
};

// ---- The plugin ----

struct Voice
{
    int key = -1, channel = 0, port = 0;
    int32_t noteId = -1;
    double phase = 0, step = 0, level = 0, env = 0;
    bool releasing = false;
};

struct Probe;
void liveStatus(Probe* s, Line& l);
void liveCommand(Probe* s, const std::string& line, const std::string& file);

constexpr uint32_t kHaveTransport = 1u << 31;   // in Probe::nowFlags, next to the transport's own flags

struct Probe
{
    clap_plugin_t plugin{};
    const clap_host_t* host = nullptr;
    const clap_host_track_info_t* hostTrackInfo = nullptr;
    const clap_host_params_t* hostParams = nullptr;
    const clap_host_state_t* hostState = nullptr;
    bool instrument = false;

    std::string instance = randomId();   // this run
    // The live thread reads these too, so they're behind textMutex.
    std::string identity = instance;     // survives save and load, so a probe can be followed across sessions
    std::string label;                    // set by whoever wrote the state (e.g. the app's round-trip check)
    std::string track, trackColor;        // from the host's track info
    std::mutex textMutex;
    unsigned saves = 0;

    std::array<std::atomic<double>, kParamCount> values{};
    double sampleRate = 48000;
    bool firstProcess = true;
    std::atomic<bool> active{ false };
    std::atomic<bool> processing{ false };

    // The live link (see live.h): what its commands asked for, and what the audio thread shows it.
    std::array<std::atomic<bool>, kParamCount> appSet{};   // knobs set by a command that the DAW hasn't been told about
    std::atomic<bool> labelChanged{ false };              // tell the DAW its state changed, on the main thread
    std::atomic<uint32_t> beepMs{ 0 };
    std::atomic<unsigned> commands{ 0 };
    std::atomic<uint32_t> nowFlags{ 0 };   // the last block's transport flags, with kHaveTransport
    std::atomic<double> nowBeats{ 0 }, nowTempo{ 0 };
    std::atomic<int> nowBar{ 0 };

    // Audio thread only.
    std::array<Voice, 16> voices{};
    clap_event_transport_t transport{};
    bool haveTransport = false;
    bool wasPlaying = false;
    double lastTempo = 0, nextBeat = NAN;
    uint16_t lastNum = 0, lastDen = 0;
    bool lastLoop = false;
    uint32_t beepLeft = 0, beepLength = 0;
    double beepPhase = 0;

    std::unique_ptr<Log> log;
    std::unique_ptr<Live> live;
    std::atomic<unsigned long long> seq{ 0 };   // orders lines from the main and audio threads

    Probe(const clap_host_t* h, bool inst) : host(h), instrument(inst)
    {
        for (int i = 0; i < kParamCount; i++)
        {
            values[i] = kParams[i].def;
            appSet[i] = false;
        }
        log = std::make_unique<Log>(inst ? "instrument" : "effect", instance);
        live = std::make_unique<Live>(
            instance, [this](Line& l) { liveStatus(this, l); },
            [this](const std::string& line, const std::string& file) { liveCommand(this, line, file); });
    }

    /// The instance is in real use: start its report file and its live status.
    void keep()
    {
        log->keep();
        live->start();
    }

    // Main-thread report line.
    template <typename F> void report(const char* event, F fill)
    {
        char buf[2048];
        Line l(buf, sizeof buf, event);
        l.integer("n", static_cast<long long>(++seq)).str("instance", instance);
        fill(l);
        log->write(l.finish());
    }

    // Audio-thread report line.
    template <typename F> void reportAudio(const char* event, F fill)
    {
        char buf[512];
        Line l(buf, sizeof buf, event);
        l.integer("n", static_cast<long long>(++seq));
        fill(l);
        log->writeAudio(l.finish());
    }
};

Probe* self(const clap_plugin_t* p) { return static_cast<Probe*>(p->plugin_data); }

const void* hostExtension(const clap_host_t* host, const char* id, const char* compat)
{
    const void* ext = host->get_extension(host, id);
    if (!ext && compat) ext = host->get_extension(host, compat);
    return ext;
}

void reportTrack(Probe* s)
{
    if (!s->hostTrackInfo) return;
    clap_track_info_t info{};
    if (!s->hostTrackInfo->get(s->host, &info)) return;
    char hex[16] = "";
    if (info.flags & CLAP_TRACK_INFO_HAS_TRACK_COLOR)
        std::snprintf(hex, sizeof hex, "#%02X%02X%02X", info.color.red, info.color.green, info.color.blue);
    {
        std::lock_guard<std::mutex> lock(s->textMutex);
        s->track = (info.flags & CLAP_TRACK_INFO_HAS_TRACK_NAME) ? info.name : "";
        s->trackColor = hex;
    }
    s->report("track", [&](Line& l) {
        if (info.flags & CLAP_TRACK_INFO_HAS_TRACK_NAME) l.str("name", info.name);
        if (info.flags & CLAP_TRACK_INFO_HAS_TRACK_COLOR) l.str("color", hex).integer("alpha", info.color.alpha);
        if (info.flags & CLAP_TRACK_INFO_HAS_AUDIO_CHANNEL)
        {
            l.integer("channels", info.audio_channel_count);
            if (info.audio_port_type) l.str("portType", info.audio_port_type);
        }
        l.boolean("return", info.flags & CLAP_TRACK_INFO_IS_FOR_RETURN_TRACK)
            .boolean("bus", info.flags & CLAP_TRACK_INFO_IS_FOR_BUS)
            .boolean("master", info.flags & CLAP_TRACK_INFO_IS_FOR_MASTER);
    });
    s->keep();
}

// -- lifecycle --

bool init(const clap_plugin_t* p)
{
    auto s = self(p);
    s->hostTrackInfo = static_cast<const clap_host_track_info_t*>(hostExtension(s->host, CLAP_EXT_TRACK_INFO, CLAP_EXT_TRACK_INFO_COMPAT));
    // For the live link: telling the DAW about knobs a command moved, and that a new label needs saving.
    s->hostParams = static_cast<const clap_host_params_t*>(hostExtension(s->host, CLAP_EXT_PARAMS, nullptr));
    if (s->hostParams && !s->hostParams->request_flush) s->hostParams = nullptr;
    s->hostState = static_cast<const clap_host_state_t*>(hostExtension(s->host, CLAP_EXT_STATE, nullptr));
    if (s->hostState && !s->hostState->mark_dirty) s->hostState = nullptr;
    s->report("create", [&](Line& l) {
        l.str("plugin", s->instrument ? kInstrumentId : kEffectId)
            .str("probeVersion", kVersion)
            .str("host", s->host->name ? s->host->name : "")
            .str("hostVendor", s->host->vendor ? s->host->vendor : "")
            .str("hostVersion", s->host->version ? s->host->version : "")
            .str("hostUrl", s->host->url ? s->host->url : "")
            .str("hostClap", (std::to_string(s->host->clap_version.major) + "." + std::to_string(s->host->clap_version.minor) + "."
                              + std::to_string(s->host->clap_version.revision)).c_str())
#if defined(_WIN32)
            .str("os", "windows")
#elif defined(__APPLE__)
            .str("os", "macos")
#else
            .str("os", "linux")
#endif
            .str("format", wrappedFormat())
            .str("binary", pluginPath());
    });
    reportTrack(s);
    return true;
}

void destroy(const clap_plugin_t* p)
{
    auto s = self(p);
    s->live.reset();   // stops the live thread, then deletes the status file and the command folder
    s->report("destroy", [&](Line& l) { l.integer("saves", s->saves); });
    delete s;   // the log's destructor writes everything out
}

bool activate(const clap_plugin_t* p, double sampleRate, uint32_t minFrames, uint32_t maxFrames)
{
    auto s = self(p);
    s->sampleRate = sampleRate;
    s->firstProcess = true;
    s->beepMs = 0;   // a beep asked for while switched off would only surprise later
    s->beepLeft = 0;
    s->active = true;
    s->report("activate", [&](Line& l) { l.num("sampleRate", sampleRate).integer("minFrames", minFrames).integer("maxFrames", maxFrames); });
    s->keep();
    return true;
}

void deactivate(const clap_plugin_t* p)
{
    auto s = self(p);
    s->active = false;
    s->processing = false;
    s->nowFlags = 0;
    s->report("deactivate", [](Line&) {});
}

bool startProcessing(const clap_plugin_t* p)
{
    self(p)->processing = true;
    return true;
}

void stopProcessing(const clap_plugin_t* p) { self(p)->processing = false; }

void reset(const clap_plugin_t* p)
{
    for (auto& v : self(p)->voices) v = Voice{};
}

// -- positions --

struct Position
{
    bool valid = false, playing = false;
    double beats = NAN, seconds = NAN, beatInBar = NAN;
    int bar = 0;
};

Position positionAt(const Probe* s, uint32_t offset)
{
    Position pos;
    if (!s->haveTransport) return pos;
    const auto& t = s->transport;
    pos.valid = true;
    pos.playing = t.flags & CLAP_TRANSPORT_IS_PLAYING;
    double sec = offset / s->sampleRate;
    if (t.flags & CLAP_TRANSPORT_HAS_BEATS_TIMELINE)
    {
        double tempo = (t.flags & CLAP_TRANSPORT_HAS_TEMPO) ? t.tempo : 0;
        pos.beats = double(t.song_pos_beats) / CLAP_BEATTIME_FACTOR + (pos.playing ? sec * tempo / 60.0 : 0);
        double barStart = double(t.bar_start) / CLAP_BEATTIME_FACTOR;
        double beatLength = (t.flags & CLAP_TRANSPORT_HAS_TIME_SIGNATURE) && t.tsig_denom ? 4.0 / t.tsig_denom : 1.0;
        pos.bar = t.bar_number + 1;
        pos.beatInBar = (pos.beats - barStart) / beatLength + 1;
    }
    if (t.flags & CLAP_TRANSPORT_HAS_SECONDS_TIMELINE)
        pos.seconds = double(t.song_pos_seconds) / CLAP_SECTIME_FACTOR + (pos.playing ? sec : 0);
    return pos;
}

void addPosition(Line& l, const Probe* s, uint32_t offset)
{
    l.integer("offset", offset);
    auto pos = positionAt(s, offset);
    if (!pos.valid) return;
    l.boolean("playing", pos.playing).num("beats", pos.beats).num("seconds", pos.seconds);
    if (pos.bar) l.integer("bar", pos.bar).num("beat", pos.beatInBar);
}

void addTransport(Line& l, const clap_event_transport_t& t)
{
    if (t.flags & CLAP_TRANSPORT_HAS_TEMPO) l.num("tempo", t.tempo);
    if (t.flags & CLAP_TRANSPORT_HAS_TIME_SIGNATURE) l.integer("tsigNum", t.tsig_num).integer("tsigDen", t.tsig_denom);
    if (t.flags & CLAP_TRANSPORT_HAS_BEATS_TIMELINE) l.num("beats", double(t.song_pos_beats) / CLAP_BEATTIME_FACTOR).integer("bar", t.bar_number + 1);
    if (t.flags & CLAP_TRANSPORT_HAS_SECONDS_TIMELINE) l.num("seconds", double(t.song_pos_seconds) / CLAP_SECTIME_FACTOR);
    l.boolean("playing", t.flags & CLAP_TRANSPORT_IS_PLAYING)
        .boolean("recording", t.flags & CLAP_TRANSPORT_IS_RECORDING)
        .boolean("loop", t.flags & CLAP_TRANSPORT_IS_LOOP_ACTIVE);
    if ((t.flags & CLAP_TRANSPORT_IS_LOOP_ACTIVE) && (t.flags & CLAP_TRANSPORT_HAS_BEATS_TIMELINE))
        l.num("loopStart", double(t.loop_start_beats) / CLAP_BEATTIME_FACTOR).num("loopEnd", double(t.loop_end_beats) / CLAP_BEATTIME_FACTOR);
}

/// Reports play, stop, tempo, time signature, loop and jumps, but not every block.
void checkTransport(Probe* s, uint32_t frames)
{
    if (!s->haveTransport) return;
    const auto& t = s->transport;
    bool playing = t.flags & CLAP_TRANSPORT_IS_PLAYING;
    bool loop = t.flags & CLAP_TRANSPORT_IS_LOOP_ACTIVE;
    double beat = (t.flags & CLAP_TRANSPORT_HAS_BEATS_TIMELINE) ? double(t.song_pos_beats) / CLAP_BEATTIME_FACTOR : NAN;

    if (playing != s->wasPlaying)
        s->reportAudio(playing ? "play" : "stop", [&](Line& l) { addTransport(l, t); });
    else if (playing && std::isfinite(s->nextBeat) && std::isfinite(beat) && std::fabs(beat - s->nextBeat) > 0.01)
        s->reportAudio("jump", [&](Line& l) { l.num("from", s->nextBeat).num("to", beat).boolean("loop", loop); });

    if ((t.flags & CLAP_TRANSPORT_HAS_TEMPO) && std::fabs(t.tempo - s->lastTempo) > 1e-6 && s->lastTempo != 0)
        s->reportAudio("tempo", [&](Line& l) { l.num("tempo", t.tempo).num("was", s->lastTempo).num("beats", beat); });
    if ((t.flags & CLAP_TRANSPORT_HAS_TIME_SIGNATURE) && s->lastNum && (t.tsig_num != s->lastNum || t.tsig_denom != s->lastDen))
        s->reportAudio("time-signature", [&](Line& l) { l.integer("num", t.tsig_num).integer("den", t.tsig_denom).num("beats", beat); });
    if (loop != s->lastLoop)
        s->reportAudio("loop", [&](Line& l) { addTransport(l, t); });

    s->wasPlaying = playing;
    s->lastLoop = loop;
    if (t.flags & CLAP_TRANSPORT_HAS_TEMPO) s->lastTempo = t.tempo;
    if (t.flags & CLAP_TRANSPORT_HAS_TIME_SIGNATURE) { s->lastNum = t.tsig_num; s->lastDen = t.tsig_denom; }
    s->nextBeat = playing && std::isfinite(beat) && (t.flags & CLAP_TRANSPORT_HAS_TEMPO)
                      ? beat + frames / s->sampleRate * t.tempo / 60.0
                      : NAN;
}

// -- notes --

void noteOn(Probe* s, int key, int channel, int port, int32_t noteId, double velocity)
{
    if (!s->instrument || key < 0 || key > 127) return;
    Voice* v = &s->voices[0];
    for (auto& candidate : s->voices)
        if (candidate.key < 0) { v = &candidate; break; }
    *v = Voice{};
    v->key = key;
    v->channel = channel;
    v->port = port;
    v->noteId = noteId;
    v->step = kTwoPi * 440.0 * std::pow(2.0, (key - 69) / 12.0) / s->sampleRate;
    v->level = 0.15 * std::clamp(velocity, 0.0, 1.0);
}

void noteOff(Probe* s, int key, int channel, int32_t noteId, bool choke)
{
    for (auto& v : s->voices)
    {
        if (v.key < 0) continue;
        bool match = noteId >= 0 ? v.noteId == noteId
                                 : (key < 0 || v.key == key) && (channel < 0 || v.channel == channel);
        if (!match) continue;
        if (choke) v.key = -1;
        else v.releasing = true;
    }
}

void handleParam(Probe* s, const clap_event_param_value_t* e, bool audio)
{
    int i = paramIndex(e->param_id);
    if (i < 0) return;
    if (e->note_id < 0 && e->key < 0) s->values[i] = std::clamp(e->value, kParams[i].min, kParams[i].max);
    auto fill = [&](Line& l) {
        l.integer("id", e->param_id).str("name", kParams[i].name).num("value", e->value);
        if (e->note_id >= 0) l.integer("noteId", e->note_id);
        if (e->key >= 0) l.integer("key", e->key).integer("channel", e->channel);
        addPosition(l, s, e->header.time);
    };
    if (audio) s->reportAudio("param", fill);
    else s->report("param", fill);
}

void handleEvent(Probe* s, const clap_event_header_t* h, bool audio, int paramCounts[kParamCount])
{
    if (h->space_id != CLAP_CORE_EVENT_SPACE_ID) return;
    auto out = [&](const char* event, auto fill) {
        if (audio) s->reportAudio(event, fill);
        else s->report(event, fill);
    };
    switch (h->type)
    {
    case CLAP_EVENT_NOTE_ON:
    case CLAP_EVENT_NOTE_OFF:
    case CLAP_EVENT_NOTE_CHOKE:
    {
        auto e = reinterpret_cast<const clap_event_note_t*>(h);
        const char* name = h->type == CLAP_EVENT_NOTE_ON ? "note-on" : h->type == CLAP_EVENT_NOTE_OFF ? "note-off" : "note-choke";
        out(name, [&](Line& l) {
            l.integer("key", e->key).integer("channel", e->channel).integer("port", e->port_index)
                .num("velocity", e->velocity).integer("noteId", e->note_id);
            addPosition(l, s, h->time);
        });
        if (h->type == CLAP_EVENT_NOTE_ON) noteOn(s, e->key, e->channel, e->port_index, e->note_id, e->velocity);
        else noteOff(s, e->key, e->channel, e->note_id, h->type == CLAP_EVENT_NOTE_CHOKE);
        break;
    }
    case CLAP_EVENT_NOTE_EXPRESSION:
    {
        auto e = reinterpret_cast<const clap_event_note_expression_t*>(h);
        static const char* const names[] = { "volume", "pan", "tuning", "vibrato", "expression", "brightness", "pressure" };
        out("note-expression", [&](Line& l) {
            l.str("expression", e->expression_id >= 0 && e->expression_id < 7 ? names[e->expression_id] : "unknown")
                .num("value", e->value).integer("key", e->key).integer("channel", e->channel).integer("noteId", e->note_id);
            addPosition(l, s, h->time);
        });
        break;
    }
    case CLAP_EVENT_PARAM_VALUE:
    {
        auto e = reinterpret_cast<const clap_event_param_value_t*>(h);
        int i = paramIndex(e->param_id);
        // Automation can arrive many times per block: report the first few and then only the last.
        if (i >= 0 && ++paramCounts[i] > 4)
        {
            if (i >= 0 && e->note_id < 0 && e->key < 0) s->values[i] = std::clamp(e->value, kParams[i].min, kParams[i].max);
            break;
        }
        handleParam(s, e, audio);
        break;
    }
    case CLAP_EVENT_PARAM_MOD:
    {
        auto e = reinterpret_cast<const clap_event_param_mod_t*>(h);
        int i = paramIndex(e->param_id);
        out("param-mod", [&](Line& l) {
            l.integer("id", e->param_id).str("name", i >= 0 ? kParams[i].name : "").num("amount", e->amount);
            if (e->note_id >= 0) l.integer("noteId", e->note_id);
            if (e->key >= 0) l.integer("key", e->key);
            addPosition(l, s, h->time);
        });
        break;
    }
    case CLAP_EVENT_PARAM_GESTURE_BEGIN:
    case CLAP_EVENT_PARAM_GESTURE_END:
    {
        auto e = reinterpret_cast<const clap_event_param_gesture_t*>(h);
        out(h->type == CLAP_EVENT_PARAM_GESTURE_BEGIN ? "gesture-begin" : "gesture-end", [&](Line& l) { l.integer("id", e->param_id); });
        break;
    }
    case CLAP_EVENT_TRANSPORT:
        s->transport = *reinterpret_cast<const clap_event_transport_t*>(h);
        s->haveTransport = true;
        break;
    case CLAP_EVENT_MIDI:
    {
        auto e = reinterpret_cast<const clap_event_midi_t*>(h);
        int status = e->data[0] & 0xF0, channel = e->data[0] & 0x0F;
        out("midi", [&](Line& l) {
            char hex[12];
            std::snprintf(hex, sizeof hex, "%02X %02X %02X", e->data[0], e->data[1], e->data[2]);
            l.str("bytes", hex).integer("port", e->port_index).integer("channel", channel);
            addPosition(l, s, h->time);
        });
        if (status == 0x90 && e->data[2] > 0) noteOn(s, e->data[1], channel, e->port_index, -1, e->data[2] / 127.0);
        else if (status == 0x80 || (status == 0x90 && e->data[2] == 0)) noteOff(s, e->data[1], channel, -1, false);
        break;
    }
    case CLAP_EVENT_MIDI_SYSEX:
    {
        auto e = reinterpret_cast<const clap_event_midi_sysex_t*>(h);
        out("sysex", [&](Line& l) {
            l.integer("size", e->size);
            addPosition(l, s, h->time);
        });
        break;
    }
    case CLAP_EVENT_MIDI2:
        out("midi2", [&](Line& l) { addPosition(l, s, h->time); });
        break;
    default:
        break;
    }
}

void handleEvents(Probe* s, const clap_input_events_t* in, bool audio)
{
    int counts[kParamCount] = {};
    for (uint32_t i = 0, n = in ? in->size(in) : 0; i < n; i++)
        if (auto h = in->get(in, i)) handleEvent(s, h, audio, counts);
    for (int i = 0; i < kParamCount; i++)
        if (counts[i] > 4)
        {
            // Say how many were left out and what the parameter ended at.
            double v = s->values[i];
            auto fill = [&](Line& l) {
                l.integer("id", kParams[i].id).str("name", kParams[i].name).num("value", v).integer("skipped", counts[i] - 4);
            };
            if (audio) s->reportAudio("param-last", fill);
            else s->report("param-last", fill);
        }
}

// -- the live link, on the audio thread --

/// What the live status shows of the transport: the last block's.
void publishTransport(Probe* s, const clap_event_transport_t* t)
{
    if (!t)
    {
        s->nowFlags.store(0, std::memory_order_relaxed);
        return;
    }
    s->nowBeats.store(double(t->song_pos_beats) / CLAP_BEATTIME_FACTOR, std::memory_order_relaxed);
    s->nowBar.store(t->bar_number + 1, std::memory_order_relaxed);
    s->nowTempo.store(t->tempo, std::memory_order_relaxed);
    s->nowFlags.store(t->flags | kHaveTransport, std::memory_order_relaxed);
}

clap_event_header_t eventHeader(uint16_t type, uint32_t size)
{
    clap_event_header_t h{};
    h.size = size;
    h.space_id = CLAP_CORE_EVENT_SPACE_ID;
    h.type = type;
    return h;
}

/// Tells the DAW about knobs a command set, as if turned by hand: gesture begin, value, gesture end,
/// so its own knob, automation recording and undo follow. From process() while processing, else from
/// params.flush() (on the main thread while not active).
void tellHost(Probe* s, const clap_output_events_t* out, bool audio, const char* via)
{
    if (!out) return;
    for (int i = 0; i < kParamCount; i++)
    {
        if (!s->appSet[i].load(std::memory_order_relaxed) || !s->appSet[i].exchange(false)) continue;
        double v = s->values[i];
        clap_event_param_gesture_t begin{}, end{};
        begin.header = eventHeader(CLAP_EVENT_PARAM_GESTURE_BEGIN, static_cast<uint32_t>(sizeof begin));
        begin.param_id = kParams[i].id;
        end.header = eventHeader(CLAP_EVENT_PARAM_GESTURE_END, static_cast<uint32_t>(sizeof end));
        end.param_id = kParams[i].id;
        clap_event_param_value_t value{};
        value.header = eventHeader(CLAP_EVENT_PARAM_VALUE, static_cast<uint32_t>(sizeof value));
        value.param_id = kParams[i].id;
        value.note_id = -1;
        value.port_index = -1;
        value.channel = -1;
        value.key = -1;
        value.value = v;
        if (!out->try_push(out, &begin.header) || !out->try_push(out, &value.header) || !out->try_push(out, &end.header))
        {
            s->appSet[i] = true;   // the DAW's queue is full: next time
            continue;
        }
        auto fill = [&](Line& l) { l.integer("id", kParams[i].id).str("name", kParams[i].name).num("value", v).str("via", via); };
        if (audio) s->reportAudio("app-set-sent", fill);
        else s->report("app-set-sent", fill);
    }
}

/// The beep command: a quiet 880 Hz tone mixed into every output channel, so you can hear which
/// track the probe is on.
void mixBeep(Probe* s, const clap_audio_buffer_t& out, uint32_t frames)
{
    if (s->beepMs.load(std::memory_order_relaxed))
        if (uint32_t ms = s->beepMs.exchange(0)) s->beepLeft = s->beepLength = static_cast<uint32_t>(ms * s->sampleRate / 1000);
    if (!s->beepLeft || !out.data32) return;
    const double step = kTwoPi * 880.0 / s->sampleRate, fade = 0.005 * s->sampleRate;
    for (uint32_t f = 0; f < frames && s->beepLeft; f++, s->beepLeft--)
    {
        // 5 ms fades at each end, so it doesn't click.
        double edge = std::min<double>(s->beepLeft, s->beepLength - s->beepLeft + 1);
        float x = static_cast<float>(0.1 * std::min(1.0, edge / fade) * std::sin(s->beepPhase));
        s->beepPhase += step;
        if (s->beepPhase > kTwoPi) s->beepPhase -= kTwoPi;
        for (uint32_t c = 0; c < out.channel_count; c++) out.data32[c][f] += x;
    }
}

clap_process_status process(const clap_plugin_t* p, const clap_process_t* proc)
{
    auto s = self(p);
    if (proc->transport)
    {
        s->transport = *proc->transport;
        s->haveTransport = true;
    }
    else s->haveTransport = false;
    publishTransport(s, proc->transport);

    if (s->firstProcess)
    {
        s->firstProcess = false;
        s->reportAudio("first-process", [&](Line& l) {
            l.integer("frames", proc->frames_count)
                .integer("inputs", proc->audio_inputs_count)
                .integer("outputs", proc->audio_outputs_count)
                .boolean("transport", proc->transport != nullptr);
            if (proc->transport) addTransport(l, *proc->transport);
        });
    }
    checkTransport(s, proc->frames_count);
    tellHost(s, proc->out_events, true, "process");
    handleEvents(s, proc->in_events, true);

    bool bypass = s->values[kBypass] >= 0.5;
    double gain = std::pow(10.0, s->values[kGain] / 20.0);
    uint32_t frames = proc->frames_count;

    if (!s->instrument)
    {
        if (proc->audio_inputs_count && proc->audio_outputs_count)
        {
            const auto& in = proc->audio_inputs[0];
            auto& out = proc->audio_outputs[0];
            for (uint32_t c = 0; c < out.channel_count; c++)
            {
                const float* src = c < in.channel_count ? in.data32[c] : nullptr;
                float* dst = out.data32[c];
                float g = bypass ? 1.0f : static_cast<float>(gain);
                for (uint32_t f = 0; f < frames; f++) dst[f] = src ? src[f] * g : 0.0f;
            }
        }
        if (proc->audio_outputs_count) mixBeep(s, proc->audio_outputs[0], frames);
        return CLAP_PROCESS_CONTINUE;
    }

    if (!proc->audio_outputs_count) return CLAP_PROCESS_CONTINUE;
    auto& out = proc->audio_outputs[0];
    for (uint32_t c = 0; c < out.channel_count; c++) std::fill(out.data32[c], out.data32[c] + frames, 0.0f);

    const double attack = 1.0 / (0.005 * s->sampleRate), release = 1.0 / (0.05 * s->sampleRate);
    for (auto& v : s->voices)
    {
        if (v.key < 0) continue;
        for (uint32_t f = 0; f < frames; f++)
        {
            if (v.releasing) v.env -= release;
            else if (v.env < 1) v.env = std::min(1.0, v.env + attack);
            if (v.env <= 0) break;
            float x = bypass ? 0.0f : static_cast<float>(std::sin(v.phase) * v.level * v.env * gain);
            v.phase += v.step;
            if (v.phase > kTwoPi) v.phase -= kTwoPi;
            for (uint32_t c = 0; c < out.channel_count; c++) out.data32[c][f] += x;
        }
        if (v.releasing && v.env <= 0)
        {
            // Tell the host the voice ended, as CLAP asks.
            clap_event_note_t end{};
            end.header.size = static_cast<uint32_t>(sizeof end);
            end.header.time = frames ? frames - 1 : 0;
            end.header.space_id = CLAP_CORE_EVENT_SPACE_ID;
            end.header.type = CLAP_EVENT_NOTE_END;
            end.note_id = v.noteId;
            end.port_index = static_cast<int16_t>(v.port);
            end.channel = static_cast<int16_t>(v.channel);
            end.key = static_cast<int16_t>(v.key);
            if (proc->out_events) proc->out_events->try_push(proc->out_events, &end.header);
            v.key = -1;
        }
    }
    mixBeep(s, out, frames);
    return CLAP_PROCESS_CONTINUE;
}

// ---- Extensions ----

// params
uint32_t paramsCount(const clap_plugin_t*) { return kParamCount; }

bool paramsInfo(const clap_plugin_t*, uint32_t index, clap_param_info_t* info)
{
    if (index >= static_cast<uint32_t>(kParamCount)) return false;
    const auto& d = kParams[index];
    *info = clap_param_info_t{};
    info->id = d.id;
    info->flags = d.flags;
    std::snprintf(info->name, sizeof info->name, "%s", d.name);
    std::snprintf(info->module, sizeof info->module, "%s", "Probe");
    info->min_value = d.min;
    info->max_value = d.max;
    info->default_value = d.def;
    return true;
}

bool paramsValue(const clap_plugin_t* p, clap_id id, double* value)
{
    int i = paramIndex(id);
    if (i < 0) return false;
    *value = self(p)->values[i];
    return true;
}

bool paramsToText(const clap_plugin_t*, clap_id id, double value, char* out, uint32_t size)
{
    int i = paramIndex(id);
    if (i < 0) return false;
    formatValue(i, value, out, size);
    return true;
}

bool paramsFromText(const clap_plugin_t*, clap_id id, const char* text, double* value)
{
    int i = paramIndex(id);
    return i >= 0 && parseValue(i, text, *value);
}

void paramsFlush(const clap_plugin_t* p, const clap_input_events_t* in, const clap_output_events_t* out)
{
    // Called on the audio thread while active, otherwise on the main thread.
    auto s = self(p);
    handleEvents(s, in, s->active);
    tellHost(s, out, s->active, "flush");
}

const clap_plugin_params_t kParamsExt = { paramsCount, paramsInfo, paramsValue, paramsToText, paramsFromText, paramsFlush };

// state
const char* contextName(uint32_t context)
{
    switch (context)
    {
    case CLAP_STATE_CONTEXT_FOR_PRESET: return "preset";
    case CLAP_STATE_CONTEXT_FOR_DUPLICATE: return "duplicate";
    case CLAP_STATE_CONTEXT_FOR_PROJECT: return "project";
    default: return "unspecified";
    }
}

/// A number for the probe's saved state, with a point whatever locale the DAW has set (snprintf writes 0,25 with a decimal comma).
std::string stateNumber(double v)
{
    char buf[40];
    std::snprintf(buf, sizeof buf, "%.10g", v);
    for (char* c = buf; *c; c++)
        if (*c == ',') *c = '.';
    return buf;
}

/// Reads a number in the probe's saved state in the C locale, so 0.25 is a quarter whatever locale the DAW has set.
double readStateNumber(const char* text)
{
    std::istringstream in(std::string(text, strnlen(text, 40)));
    in.imbue(std::locale::classic());
    double v = 0;
    in >> v;
    return v;
}

bool saveState(const clap_plugin_t* p, const clap_ostream_t* stream, uint32_t context)
{
    auto s = self(p);
    s->saves++;
    std::string text = kMarker;
    text += "\n{\"probe\":\"";
    text += s->instrument ? "instrument" : "effect";
    text += "\",\"version\":\"";
    text += kVersion;
    std::string identity, label;
    {
        std::lock_guard<std::mutex> lock(s->textMutex);
        identity = s->identity;
        label = s->label;
    }
    text += "\",\"identity\":\"" + identity + "\",\"label\":\"";
    for (char c : label)
        if (c != '"' && c != '\\' && static_cast<unsigned char>(c) >= 0x20) text += c;
    text += "\",\"instance\":\"" + s->instance + "\",\"save\":" + std::to_string(s->saves);
    text += ",\"context\":\"";
    text += contextName(context);
    text += "\",\"params\":{";
    for (int i = 0; i < kParamCount; i++)
    {
        if (i) text += ',';
        text += "\"" + std::to_string(kParams[i].id) + "\":" + stateNumber(s->values[i].load());
    }
    text += "}}\n";

    const char* data = text.data();
    int64_t left = static_cast<int64_t>(text.size());
    while (left > 0)
    {
        int64_t n = stream->write(stream, data, static_cast<uint64_t>(left));
        if (n <= 0) return false;
        data += n;
        left -= n;
    }
    s->report("save", [&](Line& l) {
        l.str("context", contextName(context)).integer("bytes", static_cast<long long>(text.size())).integer("save", s->saves)
            .str("identity", identity);
    });
    s->keep();
    return true;
}

/// Finds "key":"value" in the probe's own JSON (it never contains escapes).
std::string jsonString(const std::string& text, const char* key)
{
    std::string k = std::string("\"") + key + "\":\"";
    auto at = text.find(k);
    if (at == std::string::npos) return {};
    at += k.size();
    auto end = text.find('"', at);
    return end == std::string::npos ? std::string() : text.substr(at, end - at);
}

bool loadState(const clap_plugin_t* p, const clap_istream_t* stream, uint32_t context)
{
    auto s = self(p);
    std::string text;
    char buf[4096];
    for (;;)
    {
        int64_t n = stream->read(stream, buf, sizeof buf);
        if (n < 0) return false;
        if (n == 0) break;
        text.append(buf, static_cast<size_t>(n));
        if (text.size() > (1u << 20)) return false;
    }
    bool marker = text.compare(0, std::strlen(kMarker), kMarker) == 0;
    int restored = 0;
    std::string identity, label;
    if (marker)
    {
        for (int i = 0; i < kParamCount; i++)
        {
            std::string k = "\"" + std::to_string(kParams[i].id) + "\":";
            auto at = text.find(k);
            if (at == std::string::npos) continue;
            double v = readStateNumber(text.c_str() + at + k.size());
            s->values[i] = std::clamp(v, kParams[i].min, kParams[i].max);
            restored++;
        }
        auto id = jsonString(text, "identity");
        std::lock_guard<std::mutex> lock(s->textMutex);
        if (!id.empty()) s->identity = id;
        s->label = jsonString(text, "label");
    }
    {
        std::lock_guard<std::mutex> lock(s->textMutex);
        identity = s->identity;
        label = s->label;
    }
    std::string savedBy = jsonString(text, "instance"), savedContext = jsonString(text, "context");
    std::string save = [&] {
        auto at = text.find("\"save\":");
        return at == std::string::npos ? std::string() : std::to_string(std::atoi(text.c_str() + at + 7));
    }();
    s->report("load", [&](Line& l) {
        l.str("context", contextName(context)).integer("bytes", static_cast<long long>(text.size())).boolean("marker", marker)
            .integer("params", restored).str("identity", identity).str("label", label)
            .str("savedBy", savedBy).str("savedContext", savedContext).str("savedNumber", save);
    });
    s->keep();
    return marker;
}

bool stateSave(const clap_plugin_t* p, const clap_ostream_t* stream) { return saveState(p, stream, 0); }
bool stateLoad(const clap_plugin_t* p, const clap_istream_t* stream) { return loadState(p, stream, 0); }
const clap_plugin_state_t kStateExt = { stateSave, stateLoad };
const clap_plugin_state_context_t kStateContextExt = { saveState, loadState };

// audio ports
uint32_t audioPortsCount(const clap_plugin_t* p, bool isInput) { return isInput && self(p)->instrument ? 0 : 1; }

bool audioPortsGet(const clap_plugin_t* p, uint32_t index, bool isInput, clap_audio_port_info_t* info)
{
    if (index != 0 || (isInput && self(p)->instrument)) return false;
    *info = clap_audio_port_info_t{};
    info->id = isInput ? 0 : 1;
    std::snprintf(info->name, sizeof info->name, "%s", isInput ? "Main In" : "Main Out");
    info->flags = CLAP_AUDIO_PORT_IS_MAIN;
    info->channel_count = 2;
    info->port_type = CLAP_PORT_STEREO;
    info->in_place_pair = CLAP_INVALID_ID;
    return true;
}

const clap_plugin_audio_ports_t kAudioPortsExt = { audioPortsCount, audioPortsGet };

// note ports: both plugins take notes, so the effect can show what reaches a note-effect slot
uint32_t notePortsCount(const clap_plugin_t*, bool isInput) { return isInput ? 1 : 0; }

bool notePortsGet(const clap_plugin_t*, uint32_t index, bool isInput, clap_note_port_info_t* info)
{
    if (index != 0 || !isInput) return false;
    *info = clap_note_port_info_t{};
    info->id = 0;
    info->supported_dialects = CLAP_NOTE_DIALECT_CLAP | CLAP_NOTE_DIALECT_MIDI;
    info->preferred_dialect = CLAP_NOTE_DIALECT_CLAP;
    std::snprintf(info->name, sizeof info->name, "%s", "Notes In");
    return true;
}

const clap_plugin_note_ports_t kNotePortsExt = { notePortsCount, notePortsGet };

// track info
void trackChanged(const clap_plugin_t* p) { reportTrack(self(p)); }
const clap_plugin_track_info_t kTrackInfoExt = { trackChanged };

// remote controls: one page with all eight, so a DAW's controller mapping shows up too
uint32_t remoteCount(const clap_plugin_t*) { return 1; }

bool remoteGet(const clap_plugin_t*, uint32_t index, clap_remote_controls_page_t* page)
{
    if (index != 0) return false;
    *page = clap_remote_controls_page_t{};
    std::snprintf(page->section_name, sizeof page->section_name, "%s", "Probe");
    page->page_id = 1;
    std::snprintf(page->page_name, sizeof page->page_name, "%s", "MO Probe");
    for (int i = 0; i < CLAP_REMOTE_CONTROLS_COUNT && i < kParamCount; i++) page->param_ids[i] = kParams[i].id;
    page->is_for_preset = false;
    return true;
}

const clap_plugin_remote_controls_t kRemoteControlsExt = { remoteCount, remoteGet };

// param indication: the DAW tells us which knobs are mapped to a controller or automated
void indicateMapping(const clap_plugin_t* p, clap_id id, bool hasMapping, const clap_color_t* color, const char* label,
                     const char* description)
{
    auto s = self(p);
    int i = paramIndex(id);
    s->report("mapping", [&](Line& l) {
        l.integer("id", id).str("name", i >= 0 ? kParams[i].name : "").boolean("mapped", hasMapping);
        if (label) l.str("label", label);
        if (description) l.str("description", description);
        if (color)
        {
            char hex[16];
            std::snprintf(hex, sizeof hex, "#%02X%02X%02X", color->red, color->green, color->blue);
            l.str("color", hex);
        }
    });
}

void indicateAutomation(const clap_plugin_t* p, clap_id id, uint32_t state, const clap_color_t*)
{
    auto s = self(p);
    int i = paramIndex(id);
    static const char* const names[] = { "none", "present", "playing", "recording", "overriding" };
    s->report("automation", [&](Line& l) {
        l.integer("id", id).str("name", i >= 0 ? kParams[i].name : "").str("state", state < 5 ? names[state] : "unknown");
    });
}

const clap_plugin_param_indication_t kParamIndicationExt = { indicateMapping, indicateAutomation };

// render: tells us when the DAW bounces offline
bool renderHardRealtime(const clap_plugin_t*) { return false; }

bool renderSet(const clap_plugin_t* p, clap_plugin_render_mode mode)
{
    self(p)->report("render", [&](Line& l) { l.str("mode", mode == CLAP_RENDER_OFFLINE ? "offline" : "realtime"); });
    return true;
}

const clap_plugin_render_t kRenderExt = { renderHardRealtime, renderSet };

const void* getExtension(const clap_plugin_t*, const char* id)
{
    if (!std::strcmp(id, CLAP_EXT_PARAMS)) return &kParamsExt;
    if (!std::strcmp(id, CLAP_EXT_STATE)) return &kStateExt;
    if (!std::strcmp(id, CLAP_EXT_STATE_CONTEXT)) return &kStateContextExt;
    if (!std::strcmp(id, CLAP_EXT_AUDIO_PORTS)) return &kAudioPortsExt;
    if (!std::strcmp(id, CLAP_EXT_NOTE_PORTS)) return &kNotePortsExt;
    if (!std::strcmp(id, CLAP_EXT_TRACK_INFO) || !std::strcmp(id, CLAP_EXT_TRACK_INFO_COMPAT)) return &kTrackInfoExt;
    if (!std::strcmp(id, CLAP_EXT_REMOTE_CONTROLS) || !std::strcmp(id, CLAP_EXT_REMOTE_CONTROLS_COMPAT)) return &kRemoteControlsExt;
    if (!std::strcmp(id, CLAP_EXT_PARAM_INDICATION) || !std::strcmp(id, CLAP_EXT_PARAM_INDICATION_COMPAT)) return &kParamIndicationExt;
    if (!std::strcmp(id, CLAP_EXT_RENDER)) return &kRenderExt;
    return nullptr;
}

void onMainThread(const clap_plugin_t* p)
{
    // A label command: tell the DAW the state changed, so the label is saved with the project.
    auto s = self(p);
    if (s->labelChanged.exchange(false) && s->hostState) s->hostState->mark_dirty(s->host);
}

// ---- The live link (see live.h): runs on the live thread ----

long long processId()
{
#ifdef _WIN32
    return _getpid();
#else
    return getpid();
#endif
}

void liveStatus(Probe* s, Line& l)
{
    std::string identity, label, track, trackColor;
    {
        std::lock_guard<std::mutex> lock(s->textMutex);
        identity = s->identity;
        label = s->label;
        track = s->track;
        trackColor = s->trackColor;
    }
    l.str("probe", s->instrument ? "instrument" : "effect")
        .str("instance", s->instance)
        .str("identity", identity)
        .str("label", label)
        .str("host", s->host->name ? s->host->name : "")
        .str("hostVersion", s->host->version ? s->host->version : "")
        .str("format", wrappedFormat())
        .str("track", track)
        .str("trackColor", trackColor)
        .str("report", s->log->fileName())
        .str("t", nowIso())
        .integer("pid", processId())
        .boolean("active", s->active);
    uint32_t flags = s->nowFlags.load(std::memory_order_relaxed);
    if (flags & kHaveTransport)
    {
        l.boolean("playing", flags & CLAP_TRANSPORT_IS_PLAYING);
        if (flags & CLAP_TRANSPORT_HAS_BEATS_TIMELINE) l.num("beats", s->nowBeats.load()).integer("bar", s->nowBar.load());
        if (flags & CLAP_TRANSPORT_HAS_TEMPO) l.num("tempo", s->nowTempo.load());
    }
    l.integer("commands", s->commands);

    char buf[512];
    Line params(buf, sizeof buf);
    for (int i = 0; i < kParamCount; i++)
    {
        char id[16];
        std::snprintf(id, sizeof id, "%u", kParams[i].id);
        params.num(id, s->values[i]);
    }
    l.raw("params", params.finish());
}

/// A label as the probe keeps it: up to 64 characters, without quotes, backslashes or control characters.
std::string cleanLabel(const std::string& text)
{
    std::string out;
    int chars = 0;
    for (unsigned char c : text)
    {
        if (c == '"' || c == '\\' || c < 0x20 || c == 0x7F) continue;
        if ((c & 0xC0) != 0x80 && ++chars > 64) break;   // counts characters, not UTF-8 bytes
        out += static_cast<char>(c);
    }
    return out;
}

/// The text cut to at most `max` bytes, without splitting a character.
std::string shorten(const std::string& text, size_t max)
{
    if (text.size() <= max) return text;
    while (max > 0 && (static_cast<unsigned char>(text[max]) & 0xC0) == 0x80) max--;
    return text.substr(0, max) + "...";
}

void liveCommand(Probe* s, const std::string& line, const std::string& file)
{
    s->commands++;
    Fields fields;
    bool isObject = parseObject(line, fields);
    auto field = [&](const char* key) -> const Field* {
        auto at = fields.find(key);
        return at == fields.end() ? nullptr : &at->second;
    };
    // Every answer carries the command's "id" as "for", so the sender can find it in the report.
    const Field* id = field("id");
    auto answer = [&](const char* event, auto fill) {
        s->report(event, [&](Line& l) {
            if (id && id->kind == Field::String) l.str("for", id->text);
            fill(l);
        });
    };
    auto fail = [&](const std::string& why) {
        answer("command-error", [&](Line& l) { l.str("error", why).str("file", file).str("line", shorten(line, 300)); });
    };

    const Field* cmd = field("cmd");
    if (!isObject)
    {
        fail("not a JSON object");
        return;
    }
    if (!cmd || cmd->kind != Field::String)
    {
        fail("no \"cmd\": a command looks like {\"cmd\":\"ping\"}");
        return;
    }

    if (cmd->text == "ping")
    {
        answer("pong", [](Line&) {});
        return;
    }

    if (cmd->text == "set")
    {
        const Field* param = field("param");
        const Field* value = field("value");
        int i = -1;
        if (param && param->kind == Field::Number && param->number == std::floor(param->number) && param->number >= 0 && param->number < 1e9)
            i = paramIndex(static_cast<clap_id>(param->number));
        else if (param && param->kind == Field::String)
            i = paramIndex(static_cast<clap_id>(std::strtoul(param->text.c_str(), nullptr, 10)));
        if (i < 0)
        {
            fail("\"param\" must be a knob's ID, 1001 to 1008");
            return;
        }
        double v = 0;
        bool ok = false;
        if (value && value->kind == Field::Number)
        {
            v = value->number;
            ok = true;
        }
        // Text is read the way the knob reads it, so "On", "Bravo" or "-6 dB" work too.
        else if (value && value->kind == Field::String) ok = parseValue(i, value->text.c_str(), v);
        if (!ok || std::isnan(v))
        {
            fail("\"value\" must be a number, in the knob's own units");
            return;
        }

        const auto& d = kParams[i];
        bool clamped = v < d.min || v > d.max;
        v = std::clamp(v, d.min, d.max);
        if (d.flags & CLAP_PARAM_IS_STEPPED) v = std::round(v);
        s->values[i] = v;
        s->appSet[i] = true;
        // While processing, the next block tells the DAW; otherwise the DAW is asked for a flush.
        const char* told = s->processing ? "process" : s->hostParams ? "flush" : "waiting";
        char text[64];
        formatValue(i, v, text, sizeof text);
        answer("app-set", [&](Line& l) {
            l.integer("id", d.id).str("name", d.name).num("value", v).str("text", text);
            if (clamped) l.boolean("clamped", true);
            l.str("told", told);
        });
        if (!std::strcmp(told, "flush")) s->hostParams->request_flush(s->host);
        return;
    }

    if (cmd->text == "label")
    {
        const Field* text = field("label");
        if (!text || text->kind != Field::String)
        {
            fail("\"label\" must be text");
            return;
        }
        std::string label = cleanLabel(text->text);
        {
            std::lock_guard<std::mutex> lock(s->textMutex);
            s->label = label;
        }
        // The DAW is told the state changed from on_main_thread, where CLAP allows it.
        s->labelChanged = true;
        answer("label", [&](Line& l) { l.str("label", label); });
        if (s->host->request_callback) s->host->request_callback(s->host);
        return;
    }

    if (cmd->text == "beep")
    {
        const Field* seconds = field("seconds");
        if (seconds && seconds->kind != Field::Number && seconds->kind != Field::Null)
        {
            fail("\"seconds\" must be a number");
            return;
        }
        double length = seconds && seconds->kind == Field::Number ? std::clamp(seconds->number, 0.0, 2.0) : 0.3;
        s->beepMs = static_cast<uint32_t>(std::lround(length * 1000));
        answer("beep", [&](Line& l) { l.num("seconds", length); });
        return;
    }

    fail("unknown command \"" + cmd->text + "\"");
}

// ---- Factory ----

uint32_t factoryCount(const clap_plugin_factory_t*) { return 2; }

const clap_plugin_descriptor_t* factoryDescriptor(const clap_plugin_factory_t*, uint32_t index)
{
    return index < 2 ? &kDescriptors[index] : nullptr;
}

const clap_plugin_t* factoryCreate(const clap_plugin_factory_t*, const clap_host_t* host, const char* id)
{
    if (!host || !clap_version_is_compatible(host->clap_version)) return nullptr;
    int which = !std::strcmp(id, kInstrumentId) ? 0 : !std::strcmp(id, kEffectId) ? 1 : -1;
    if (which < 0) return nullptr;
    auto s = new Probe(host, which == 0);
    s->plugin.desc = &kDescriptors[which];
    s->plugin.plugin_data = s;
    s->plugin.init = init;
    s->plugin.destroy = destroy;
    s->plugin.activate = activate;
    s->plugin.deactivate = deactivate;
    s->plugin.start_processing = startProcessing;
    s->plugin.stop_processing = stopProcessing;
    s->plugin.reset = reset;
    s->plugin.process = process;
    s->plugin.get_extension = getExtension;
    s->plugin.on_main_thread = onMainThread;
    return &s->plugin;
}

const clap_plugin_factory_t kFactory = { factoryCount, factoryDescriptor, factoryCreate };

#ifdef MO_PROBE_WRAPPED
// Fixed IDs, so the VST3 and AU versions keep the same identity in every build and on every OS.
const array_of_16_bytes kInstrumentUid = COMPONENT_ID(0xB0DDF78C, 0x9D004BA9, 0xB2359D3E, 0x7B74D7F1);
const array_of_16_bytes kEffectUid = COMPONENT_ID(0x805ECA49, 0x563E43EA, 0x8119369F, 0x0ED6096D);
const clap_plugin_info_as_vst3_t kVst3Info[] = {
    { "Music Organiser", &kInstrumentUid, "Instrument|Synth" },
    { "Music Organiser", &kEffectUid, "Fx|Tools" },
};

const clap_plugin_info_as_vst3_t* vst3Info(const clap_plugin_factory_as_vst3_t*, uint32_t index)
{
    return index < 2 ? &kVst3Info[index] : nullptr;
}

const clap_plugin_factory_as_vst3_t kVst3Factory = { "Music Organiser", "", "", vst3Info, nullptr };

bool auv2Info(const clap_plugin_factory_as_auv2_t*, uint32_t index, clap_plugin_info_as_auv2_t* info)
{
    if (index >= 2) return false;
    std::snprintf(info->au_type, sizeof info->au_type, "%s", index == 0 ? "aumu" : "aufx");
    std::snprintf(info->au_subt, sizeof info->au_subt, "%s", index == 0 ? "PrbI" : "PrbE");
    return true;
}

const clap_plugin_factory_as_auv2_t kAuv2Factory = { "MOrg", "Music Organiser", auv2Info };
#endif

std::string g_path;
std::string g_format = "CLAP";

} // namespace

const char* pluginPath() { return g_path.c_str(); }
const char* wrappedFormat() { return g_format.c_str(); }

bool entryInit(const char* path)
{
    g_path = path ? path : "";
    // The wrappers load the same code; tell the report which format the DAW is using.
    auto lower = g_path;
    std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    if (lower.find(".vst3") != std::string::npos) g_format = "VST3";
    else if (lower.find(".component") != std::string::npos) g_format = "AU";
    else g_format = "CLAP";
    return true;
}

void entryDeinit() {}

const void* entryFactory(const char* id)
{
    if (!std::strcmp(id, CLAP_PLUGIN_FACTORY_ID)) return &kFactory;
#ifdef MO_PROBE_WRAPPED
    if (!std::strcmp(id, CLAP_PLUGIN_FACTORY_INFO_VST3) || !std::strcmp(id, CLAP_PLUGIN_FACTORY_INFO_VST3_V1)) return &kVst3Factory;
    if (!std::strcmp(id, CLAP_PLUGIN_FACTORY_INFO_AUV2)) return &kAuv2Factory;
#endif
    return nullptr;
}

} // namespace probe
