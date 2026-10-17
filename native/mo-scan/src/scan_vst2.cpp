// VST2: load the plugin through the VST 2.4 binary interface and read its names, parameters and state.
// Steinberg no longer licenses the VST2 SDK, so the few structures and opcode numbers a host needs
// are declared here from the binary interface that every VST2 plugin exports.
#include "json.h"
#include "module.h"

#include <cstdint>
#include <cstring>
#include <vector>

namespace mo {
namespace {

#if defined(_WIN32)
#define VSTCALL __cdecl
#else
#define VSTCALL
#endif

struct AEffect;
using HostCallback = intptr_t(VSTCALL*)(AEffect*, int32_t opcode, int32_t index, intptr_t value, void* ptr, float opt);

struct AEffect
{
    int32_t magic;   // 'VstP'
    intptr_t(VSTCALL* dispatcher)(AEffect*, int32_t opcode, int32_t index, intptr_t value, void* ptr, float opt);
    void(VSTCALL* process)(AEffect*, float**, float**, int32_t);
    void(VSTCALL* setParameter)(AEffect*, int32_t index, float value);
    float(VSTCALL* getParameter)(AEffect*, int32_t index);
    int32_t numPrograms;
    int32_t numParams;
    int32_t numInputs;
    int32_t numOutputs;
    int32_t flags;
    intptr_t reserved1;
    intptr_t reserved2;
    int32_t initialDelay;
    int32_t realQualities;
    int32_t offQualities;
    float ioRatio;
    void* object;
    void* user;
    int32_t uniqueID;
    int32_t version;
    void(VSTCALL* processReplacing)(AEffect*, float**, float**, int32_t);
    void(VSTCALL* processDoubleReplacing)(AEffect*, double**, double**, int32_t);
    char future[56];
};

constexpr int32_t kMagic = ('V' << 24) | ('s' << 16) | ('t' << 8) | 'P';

// Plugin opcodes.
enum : int32_t
{
    effOpen = 0, effClose = 1, effGetParamLabel = 6, effGetParamDisplay = 7, effGetParamName = 8,
    effSetSampleRate = 10, effSetBlockSize = 11, effGetChunk = 23, effGetProgramNameIndexed = 29,
    effGetPlugCategory = 35, effGetEffectName = 45, effGetVendorString = 47, effGetProductString = 48,
    effGetVendorVersion = 49, effGetVstVersion = 58, effShellGetNextPlugin = 70,
};

// Host opcodes.
enum : int32_t
{
    audioMasterAutomate = 0, audioMasterVersion = 1, audioMasterCurrentId = 2, audioMasterGetSampleRate = 16,
    audioMasterGetBlockSize = 17, audioMasterGetVendorString = 32, audioMasterGetProductString = 33,
    audioMasterGetVendorVersion = 34, audioMasterCanDo = 37, audioMasterGetLanguage = 38,
};

constexpr int32_t effFlagsHasEditor = 1 << 0;
constexpr int32_t effFlagsProgramChunks = 1 << 5;
constexpr int32_t effFlagsIsSynth = 1 << 8;
constexpr intptr_t kPlugCategShell = 10;

int32_t g_currentId = 0;   // which plugin a shell should create next

intptr_t VSTCALL hostCallback(AEffect*, int32_t opcode, int32_t, intptr_t, void* ptr, float)
{
    switch (opcode)
    {
    case audioMasterVersion: return 2400;
    case audioMasterCurrentId: return g_currentId;
    case audioMasterGetSampleRate: return 48000;
    case audioMasterGetBlockSize: return 512;
    case audioMasterGetVendorString: std::strcpy(static_cast<char*>(ptr), "Music Organiser"); return 1;
    case audioMasterGetProductString: std::strcpy(static_cast<char*>(ptr), "Music Organiser scanner"); return 1;
    case audioMasterGetVendorVersion: return 1000;
    case audioMasterGetLanguage: return 1;   // English
    default: return 0;
    }
}

using Entry = AEffect*(VSTCALL*)(HostCallback);

/// Asks for a string with a generous buffer, since plugins often write past the documented sizes.
std::string ask(AEffect* e, int32_t opcode, int32_t index = 0)
{
    char buf[1024] = {};
    e->dispatcher(e, opcode, index, 0, buf, 0);
    return cleanUtf8(buf, sizeof buf - 1);
}

std::string fourCC(int32_t id)
{
    char s[5] = { char((id >> 24) & 0xFF), char((id >> 16) & 0xFF), char((id >> 8) & 0xFF), char(id & 0xFF), 0 };
    for (int i = 0; i < 4; i++)
        if (s[i] < 0x20 || s[i] > 0x7E) return std::to_string(id);
    return s;
}

void writePlugin(Json& j, const std::filesystem::path& path, AEffect* e, const Options& opt, const std::string& shellName)
{
    std::string name = ask(e, effGetEffectName);
    if (name.empty()) name = shellName;
    if (name.empty()) name = ask(e, effGetProductString);
    if (name.empty()) name = path.stem().u8string();
    int32_t vendorVersion = static_cast<int32_t>(e->dispatcher(e, effGetVendorVersion, 0, 0, nullptr, 0));

    j.str("format", "VST2")
        .str("path", path.u8string())
        .str("id", fourCC(e->uniqueID))
        .integer("uniqueId", e->uniqueID)
        .str("name", name)
        .str("vendor", ask(e, effGetVendorString))
        .str("version", std::to_string(vendorVersion))
        .str("kind", (e->flags & effFlagsIsSynth) ? "instrument" : "effect")
        .integer("audioIn", e->numInputs)
        .integer("audioOut", e->numOutputs)
        .integer("programs", e->numPrograms)
        .integer("latency", e->initialDelay)
        .boolean("chunks", e->flags & effFlagsProgramChunks)
        .boolean("editor", e->flags & effFlagsHasEditor);

    if (opt.params)
    {
        j.beginArray("params");
        for (int32_t i = 0; i < e->numParams; i++)
        {
            j.item()
                .integer("id", i)
                .str("name", ask(e, effGetParamName, i))
                .str("units", ask(e, effGetParamLabel, i))
                .num("value", e->getParameter(e, i))
                .str("text", ask(e, effGetParamDisplay, i))
                .end();
        }
        j.endArray();
    }

    if (opt.state)
    {
        std::vector<uint8_t> bytes;
        if (e->flags & effFlagsProgramChunks)
        {
            void* chunk = nullptr;
            auto size = e->dispatcher(e, effGetChunk, 0, 0, &chunk, 0);   // 0 = whole bank
            if (chunk && size > 0) bytes.assign(static_cast<uint8_t*>(chunk), static_cast<uint8_t*>(chunk) + size);
        }
        else
        {
            for (int32_t i = 0; i < e->numParams; i++)
            {
                float v = e->getParameter(e, i);
                auto p = reinterpret_cast<uint8_t*>(&v);
                bytes.insert(bytes.end(), p, p + sizeof v);
            }
        }
        if (!bytes.empty()) j.str("state", base64(bytes));
    }
}

AEffect* open(Entry entry)
{
    AEffect* e = entry(hostCallback);
    if (!e || e->magic != kMagic) return nullptr;
    e->dispatcher(e, effOpen, 0, 0, nullptr, 0);
    e->dispatcher(e, effSetSampleRate, 0, 0, nullptr, 48000.0f);
    e->dispatcher(e, effSetBlockSize, 0, 512, nullptr, 0);
    return e;
}

} // namespace

bool scanVst2(const std::filesystem::path& path, const Options& opt, std::string& error)
{
    Library lib;
    if (!lib.open(path, error))
    {
#ifdef _WIN32
        if (error.find("error 193") != std::string::npos) error = "32-bit plugin: this scanner is 64-bit";
#endif
        return false;
    }
    auto entry = reinterpret_cast<Entry>(lib.symbol("VSTPluginMain"));
    if (!entry) entry = reinterpret_cast<Entry>(lib.symbol("main_macho"));
    if (!entry) entry = reinterpret_cast<Entry>(lib.symbol("main"));
    if (!entry) { error = "not a VST2 plugin (no VSTPluginMain)"; return false; }

    AEffect* e = open(entry);
    if (!e) { error = "the plugin didn't start"; return false; }

    if (e->dispatcher(e, effGetPlugCategory, 0, 0, nullptr, 0) == kPlugCategShell)
    {
        // A shell (e.g. Waves) holds many plugins. List them; open each one by its ID only when its
        // parameters or state are wanted, since opening hundreds takes minutes.
        std::vector<std::pair<int32_t, std::string>> subs;
        for (int guard = 0; guard < 4096; guard++)
        {
            char name[1024] = {};
            auto id = static_cast<int32_t>(e->dispatcher(e, effShellGetNextPlugin, 0, 0, name, 0));
            if (id == 0) break;
            if (opt.wanted(fourCC(id))) subs.emplace_back(id, cleanUtf8(name, sizeof name - 1));
        }
        std::string vendor = ask(e, effGetVendorString);
        e->dispatcher(e, effClose, 0, 0, nullptr, 0);
        for (auto& [id, name] : subs)
        {
            Json j;
            j.begin().str("shell", path.stem().u8string());
            if (!opt.params && !opt.state)
            {
                j.str("format", "VST2").str("path", path.u8string()).str("id", fourCC(id)).integer("uniqueId", id)
                    .str("name", name).str("vendor", vendor);
                emit(j.end());
                continue;
            }
            g_currentId = id;
            if (AEffect* sub = open(entry))
            {
                writePlugin(j, path, sub, opt, name);
                sub->dispatcher(sub, effClose, 0, 0, nullptr, 0);
            }
            else j.str("format", "VST2").str("path", path.u8string()).str("id", fourCC(id)).str("name", name).str("error", "couldn't open");
            emit(j.end());
        }
        g_currentId = 0;
        return true;
    }

    Json j;
    j.begin();
    writePlugin(j, path, e, opt, "");
    emit(j.end());
    e->dispatcher(e, effClose, 0, 0, nullptr, 0);
    return true;
}

} // namespace mo
