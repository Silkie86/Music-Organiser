// A stand-in VST2 plugin for testing mo-scan's VST2 reader, since no real VST2 plugins can be
// installed on the build machines. It's a "shell" like Waves': one file holding two plugins,
// "Fake Synth" (MoA1) and "Fake Delay" (MoB2), each with three parameters.
//
// It declares the VST 2.4 binary interface itself, the same way scan_vst2.cpp does.
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

#if defined(_WIN32)
#define VSTCALL __cdecl
#define FAKE_EXPORT extern "C" __declspec(dllexport)
#else
#define VSTCALL
#define FAKE_EXPORT extern "C" __attribute__((visibility("default")))
#endif

struct AEffect;
using HostCallback = intptr_t(VSTCALL*)(AEffect*, int32_t opcode, int32_t index, intptr_t value, void* ptr, float opt);

struct AEffect
{
    int32_t magic;
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

namespace {

constexpr int32_t fourCC(const char (&s)[5])
{
    return (int32_t(uint8_t(s[0])) << 24) | (int32_t(uint8_t(s[1])) << 16) | (int32_t(uint8_t(s[2])) << 8) | int32_t(uint8_t(s[3]));
}

struct Sub
{
    int32_t id;
    const char* name;
    bool synth;
};

const Sub kSubs[] = { { fourCC("MoA1"), "Fake Synth", true }, { fourCC("MoB2"), "Fake Delay", false } };
const char* kParamNames[] = { "Cutoff", "Mix", "Mode" };
const char* kParamUnits[] = { "Hz", "%", "" };

struct Instance
{
    AEffect effect{};
    const Sub* sub = nullptr;   // null for the shell itself
    int next = 0;               // the shell's position in its list
    float params[3] = { 0.5f, 0.25f, 0.0f };
};

Instance* self(AEffect* e) { return static_cast<Instance*>(e->object); }

void copy(void* ptr, const char* text) { std::strcpy(static_cast<char*>(ptr), text); }

intptr_t VSTCALL dispatch(AEffect* e, int32_t opcode, int32_t index, intptr_t value, void* ptr, float)
{
    Instance* in = self(e);
    switch (opcode)
    {
    case 1: delete in; return 1;                                              // effClose
    case 6: if (index >= 0 && index < 3) copy(ptr, kParamUnits[index]); return 1;   // effGetParamLabel
    case 7:                                                                   // effGetParamDisplay
        if (index >= 0 && index < 3) std::snprintf(static_cast<char*>(ptr), 8, "%.2f", in->params[index]);
        return 1;
    case 8: if (index >= 0 && index < 3) copy(ptr, kParamNames[index]); return 1;    // effGetParamName
    case 24:                                                                  // effSetChunk: a bank sets Mix, a program Mode
        if (ptr && value > 0) in->params[index ? 2 : 1] = float(std::atof(std::string(static_cast<char*>(ptr), size_t(value)).c_str()));
        return 1;
    case 35: return in->sub ? (in->sub->synth ? 2 : 1) : 10;                  // effGetPlugCategory: synth, effect, shell
    case 45: if (in->sub) { copy(ptr, in->sub->name); return 1; } return 0;   // effGetEffectName
    case 47: copy(ptr, "Fake Vendor"); return 1;                              // effGetVendorString
    case 48: copy(ptr, "Fake Shell"); return 1;                               // effGetProductString
    case 49: return 1200;                                                     // effGetVendorVersion
    case 58: return 2400;                                                     // effGetVstVersion
    case 70:                                                                  // effShellGetNextPlugin
        if (in->sub || in->next >= 2) return 0;
        copy(ptr, kSubs[in->next].name);
        return kSubs[in->next++].id;
    default: return 0;
    }
}

void VSTCALL setParameter(AEffect* e, int32_t index, float value)
{
    if (index >= 0 && index < 3) self(e)->params[index] = value;
}

float VSTCALL getParameter(AEffect* e, int32_t index) { return index >= 0 && index < 3 ? self(e)->params[index] : 0.0f; }

} // namespace

FAKE_EXPORT AEffect* VSTPluginMain(HostCallback host)
{
    // A shell is asked again for each plugin inside it, with the plugin's ID as audioMasterCurrentId.
    auto want = static_cast<int32_t>(host(nullptr, 2, 0, 0, nullptr, 0));
    auto in = new Instance;
    for (auto& s : kSubs)
        if (s.id == want) in->sub = &s;

    AEffect& e = in->effect;
    e.magic = fourCC("VstP");
    e.dispatcher = dispatch;
    e.setParameter = setParameter;
    e.getParameter = getParameter;
    e.numPrograms = 1;
    e.numParams = in->sub ? 3 : 0;
    e.numInputs = in->sub && !in->sub->synth ? 2 : 0;
    e.numOutputs = 2;
    e.flags = in->sub && in->sub->synth ? (1 << 8) : 0;   // effFlagsIsSynth
    e.object = in;
    e.uniqueID = in->sub ? in->sub->id : fourCC("MoSh");
    e.version = 1200;
    return &e;
}
