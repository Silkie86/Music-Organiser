// VST3: load the module, list its audio classes, and read buses, parameters and state.
#include "json.h"
#include "module.h"

#include <pluginterfaces/base/ibstream.h>
#include <pluginterfaces/base/ipluginbase.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>
#include <pluginterfaces/vst/ivstcomponent.h>
#include <pluginterfaces/vst/ivsteditcontroller.h>
#include <pluginterfaces/vst/ivsthostapplication.h>
#include <pluginterfaces/vst/ivstmessage.h>
#include <pluginterfaces/vst/ivstunits.h>

#include <cstdio>
#include <cstring>
#include <vector>

using namespace Steinberg;

namespace mo {
namespace {

bool sameIid(const TUID a, const TUID b) { return std::memcmp(a, b, sizeof(TUID)) == 0; }

/// Our host objects live for the whole scan, so reference counting is a no-op.
class HostApp : public Vst::IHostApplication
{
public:
    tresult PLUGIN_API getName(Vst::String128 name) override
    {
        const char* n = "Music Organiser scanner";
        size_t i = 0;
        for (; n[i] && i < 127; i++) name[i] = static_cast<Vst::TChar>(n[i]);
        name[i] = 0;
        return kResultOk;
    }
    tresult PLUGIN_API createInstance(TUID, TUID, void** obj) override
    {
        *obj = nullptr;
        return kResultFalse;
    }
    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
    {
        if (sameIid(iid, FUnknown_iid) || sameIid(iid, Vst::IHostApplication_iid)) { *obj = this; return kResultOk; }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};

class Handler : public Vst::IComponentHandler
{
public:
    tresult PLUGIN_API beginEdit(Vst::ParamID) override { return kResultOk; }
    tresult PLUGIN_API performEdit(Vst::ParamID, Vst::ParamValue) override { return kResultOk; }
    tresult PLUGIN_API endEdit(Vst::ParamID) override { return kResultOk; }
    tresult PLUGIN_API restartComponent(int32) override { return kResultOk; }
    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
    {
        if (sameIid(iid, FUnknown_iid) || sameIid(iid, Vst::IComponentHandler_iid)) { *obj = this; return kResultOk; }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};

class MemoryStream : public IBStream
{
public:
    std::vector<uint8_t> data;
    int64 pos = 0;

    tresult PLUGIN_API read(void* buffer, int32 n, int32* done) override
    {
        int64 left = static_cast<int64>(data.size()) - pos;
        int32 count = static_cast<int32>(n < left ? n : (left > 0 ? left : 0));
        if (count > 0) std::memcpy(buffer, data.data() + pos, count);
        pos += count;
        if (done) *done = count;
        return kResultOk;
    }
    tresult PLUGIN_API write(void* buffer, int32 n, int32* done) override
    {
        if (n < 0) return kInvalidArgument;
        if (pos + n > static_cast<int64>(data.size())) data.resize(static_cast<size_t>(pos + n));
        std::memcpy(data.data() + pos, buffer, n);
        pos += n;
        if (done) *done = n;
        return kResultOk;
    }
    tresult PLUGIN_API seek(int64 to, int32 mode, int64* result) override
    {
        int64 base = mode == kIBSeekCur ? pos : mode == kIBSeekEnd ? static_cast<int64>(data.size()) : 0;
        if (base + to < 0) return kInvalidArgument;
        pos = base + to;
        if (result) *result = pos;
        return kResultOk;
    }
    tresult PLUGIN_API tell(int64* p) override
    {
        if (p) *p = pos;
        return kResultOk;
    }
    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
    {
        if (sameIid(iid, FUnknown_iid) || sameIid(iid, IBStream_iid)) { *obj = this; return kResultOk; }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};

HostApp g_host;
Handler g_handler;

/// The class ID as the four 32-bit words, the way VST3 tools and DAW files usually print it.
std::string classId(const TUID cid)
{
    auto id = FUID::fromTUID(cid);
    char buf[40];
    std::snprintf(buf, sizeof buf, "%08X%08X%08X%08X", id.getLong1(), id.getLong2(), id.getLong3(), id.getLong4());
    return buf;
}

std::string str128(const Vst::String128 s) { return utf16ToUtf8(reinterpret_cast<const char16_t*>(s), 128); }

int busChannels(Vst::IComponent* c, Vst::BusDirection dir)
{
    int channels = 0;
    for (int32 i = 0, n = c->getBusCount(Vst::kAudio, dir); i < n; i++)
    {
        Vst::BusInfo info{};
        if (c->getBusInfo(Vst::kAudio, dir, i, info) == kResultOk) channels += info.channelCount;
    }
    return channels;
}

void writeParams(Json& j, Vst::IEditController* ctrl)
{
    j.beginArray("params");
    for (int32 i = 0, n = ctrl->getParameterCount(); i < n; i++)
    {
        Vst::ParameterInfo info{};
        if (ctrl->getParameterInfo(i, info) != kResultOk) continue;
        double value = ctrl->getParamNormalized(info.id);
        j.item()
            .integer("id", info.id)
            .str("name", str128(info.title))
            .str("shortName", str128(info.shortTitle))
            .str("units", str128(info.units))
            .integer("steps", info.stepCount)
            .num("default", info.defaultNormalizedValue)
            .num("value", value)
            .num("plainMin", ctrl->normalizedParamToPlain(info.id, 0))
            .num("plainMax", ctrl->normalizedParamToPlain(info.id, 1))
            .num("plainDefault", ctrl->normalizedParamToPlain(info.id, info.defaultNormalizedValue));
        Vst::String128 text{};
        if (ctrl->getParamStringByValue(info.id, value, text) == kResultOk) j.str("text", str128(text));
        j.integer("unit", info.unitId)
            .boolean("automatable", info.flags & Vst::ParameterInfo::kCanAutomate)
            .boolean("readOnly", info.flags & Vst::ParameterInfo::kIsReadOnly)
            .boolean("list", info.flags & Vst::ParameterInfo::kIsList)
            .boolean("hidden", info.flags & Vst::ParameterInfo::kIsHidden)
            .boolean("bypass", info.flags & Vst::ParameterInfo::kIsBypass)
            .boolean("program", info.flags & Vst::ParameterInfo::kIsProgramChange)
            .end();
    }
    j.endArray();
}

void scanClass(Json& j, IPluginFactory* factory, const TUID cid, const Options& opt)
{
    Vst::IComponent* comp = nullptr;
    if (factory->createInstance(cid, Vst::IComponent_iid, reinterpret_cast<void**>(&comp)) != kResultOk || !comp)
    {
        j.str("error", "couldn't create the plugin");
        return;
    }
    if (comp->initialize(&g_host) != kResultOk)
    {
        comp->release();
        j.str("error", "the plugin didn't initialise");
        return;
    }

    j.integer("audioIn", busChannels(comp, Vst::kInput))
        .integer("audioOut", busChannels(comp, Vst::kOutput))
        .integer("noteIn", comp->getBusCount(Vst::kEvent, Vst::kInput))
        .integer("noteOut", comp->getBusCount(Vst::kEvent, Vst::kOutput));

    // The controller is either the component itself or a separate class.
    Vst::IEditController* ctrl = nullptr;
    bool separate = false;
    if (comp->queryInterface(Vst::IEditController_iid, reinterpret_cast<void**>(&ctrl)) != kResultOk || !ctrl)
    {
        ctrl = nullptr;
        TUID ccid{};
        if (comp->getControllerClassId(ccid) == kResultOk
            && factory->createInstance(ccid, Vst::IEditController_iid, reinterpret_cast<void**>(&ctrl)) == kResultOk && ctrl)
        {
            separate = true;
            if (ctrl->initialize(&g_host) != kResultOk) { ctrl->release(); ctrl = nullptr; }
        }
    }

    Vst::IConnectionPoint* compCp = nullptr;
    Vst::IConnectionPoint* ctrlCp = nullptr;
    if (ctrl && separate)
    {
        comp->queryInterface(Vst::IConnectionPoint_iid, reinterpret_cast<void**>(&compCp));
        ctrl->queryInterface(Vst::IConnectionPoint_iid, reinterpret_cast<void**>(&ctrlCp));
        if (compCp && ctrlCp) { compCp->connect(ctrlCp); ctrlCp->connect(compCp); }
    }

    MemoryStream state;
    bool haveState = comp->getState(&state) == kResultOk;
    if (ctrl)
    {
        ctrl->setComponentHandler(&g_handler);
        if (haveState) { state.seek(0, IBStream::kIBSeekSet, nullptr); ctrl->setComponentState(&state); }
        if (opt.params) writeParams(j, ctrl);
    }
    else if (opt.params) j.str("paramsError", "no edit controller");

    if (opt.state)
    {
        if (haveState) j.str("state", base64(state.data));
        MemoryStream ctrlState;
        if (ctrl && ctrl->getState(&ctrlState) == kResultOk) j.str("controllerState", base64(ctrlState.data));
    }

    if (compCp && ctrlCp) { compCp->disconnect(ctrlCp); ctrlCp->disconnect(compCp); }
    if (compCp) compCp->release();
    if (ctrlCp) ctrlCp->release();
    if (ctrl)
    {
        if (separate) ctrl->terminate();
        ctrl->release();
    }
    comp->terminate();
    comp->release();
}

std::filesystem::path binaryIn(const std::filesystem::path& bundle)
{
    if (!std::filesystem::is_directory(bundle)) return bundle;   // an old single-file .vst3
#if defined(__APPLE__)
    return bundle;   // loaded as a bundle
#else
    auto file = bundle.stem();
#if defined(_WIN32)
#if defined(_M_ARM64) || defined(__aarch64__)
    const char* archs[] = { "arm64-win", "arm64ec-win", "arm64x-win" };
#else
    const char* archs[] = { "x86_64-win" };
#endif
    file += ".vst3";
#else
#if defined(__aarch64__)
    const char* archs[] = { "aarch64-linux" };
#else
    const char* archs[] = { "x86_64-linux" };
#endif
    file += ".so";
#endif
    for (auto arch : archs)
    {
        auto p = bundle / "Contents" / arch / file;
        if (std::filesystem::exists(p)) return p;
    }
    return bundle / "Contents" / archs[0] / file;
#endif
}

} // namespace

bool scanVst3(const std::filesystem::path& path, const Options& opt, std::string& error)
{
    Library lib;
    if (!lib.open(binaryIn(path), error)) return false;

#if defined(_WIN32)
    if (auto init = reinterpret_cast<bool (*)()>(lib.symbol("InitDll"))) init();
#elif defined(__APPLE__)
    if (auto init = reinterpret_cast<bool (*)(void*)>(lib.symbol("bundleEntry"))) init(lib.bundle());
#else
    if (auto init = reinterpret_cast<bool (*)(void*)>(lib.symbol("ModuleEntry"))) init(lib.handle());
#endif

    auto get = reinterpret_cast<IPluginFactory* (PLUGIN_API*)()>(lib.symbol("GetPluginFactory"));
    if (!get) { error = "no GetPluginFactory"; return false; }
    IPluginFactory* factory = get();
    if (!factory) { error = "GetPluginFactory returned nothing"; return false; }

    PFactoryInfo fi{};
    factory->getFactoryInfo(&fi);
    std::string factoryVendor = cleanUtf8(fi.vendor, sizeof fi.vendor);

    IPluginFactory2* f2 = nullptr;
    factory->queryInterface(IPluginFactory2_iid, reinterpret_cast<void**>(&f2));

    for (int32 i = 0, n = factory->countClasses(); i < n; i++)
    {
        PClassInfo info{};
        if (factory->getClassInfo(i, &info) != kResultOk) continue;
        if (std::strncmp(info.category, kVstAudioEffectClass, sizeof info.category) != 0) continue;
        if (!opt.wanted(classId(info.cid))) continue;

        std::string vendor = factoryVendor, version, sub;
        PClassInfo2 info2{};
        if (f2 && f2->getClassInfo2(i, &info2) == kResultOk)
        {
            if (info2.vendor[0]) vendor = cleanUtf8(info2.vendor, sizeof info2.vendor);
            version = cleanUtf8(info2.version, sizeof info2.version);
            sub = cleanUtf8(info2.subCategories, sizeof info2.subCategories);
        }
        std::vector<std::string> features;
        for (size_t start = 0; start <= sub.size() && !sub.empty();)
        {
            auto bar = sub.find('|', start);
            features.push_back(sub.substr(start, bar == std::string::npos ? std::string::npos : bar - start));
            if (bar == std::string::npos) break;
            start = bar + 1;
        }
        bool instrument = sub.find("Instrument") != std::string::npos;

        Json j;
        j.begin()
            .str("format", "VST3")
            .str("path", path.u8string())
            .str("id", classId(info.cid))
            .str("name", cleanUtf8(info.name, sizeof info.name))
            .str("vendor", vendor)
            .str("version", version)
            .str("kind", instrument ? "instrument" : "effect")
            .strings("features", features);
        if (opt.params || opt.state) scanClass(j, factory, info.cid, opt);
        emit(j.end());
    }

    if (f2) f2->release();
    factory->release();
#if defined(_WIN32)
    if (auto exit = reinterpret_cast<bool (*)()>(lib.symbol("ExitDll"))) exit();
#elif defined(__APPLE__)
    if (auto exit = reinterpret_cast<bool (*)()>(lib.symbol("bundleExit"))) exit();
#else
    if (auto exit = reinterpret_cast<bool (*)()>(lib.symbol("ModuleExit"))) exit();
#endif
    return true;
}

} // namespace mo
