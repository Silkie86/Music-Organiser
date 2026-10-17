// mo-scan: loads one plugin file and prints what's inside as JSON lines, one per plugin.
// The app runs it once per plugin with a timeout, so a plugin that crashes or hangs only ends this process.
//
//   mo-scan [--no-params] [--state] [--curve <steps>] [--load <state file> [--load-program]] [--only <plugin id>] [--format clap|vst3|vst2] <plugin path>
//   mo-scan --version
#include "json.h"
#include "module.h"

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

namespace mo {

bool Options::wanted(const std::string& id) const
{
    if (only.empty() || only == id) return true;
    // VST3 class IDs are hex, so their case doesn't matter. CLAP and VST2 IDs are compared exactly.
    if (only.size() != 32 || id.size() != 32) return false;
    for (size_t i = 0; i < id.size(); i++)
    {
        auto a = static_cast<unsigned char>(only[i]), b = static_cast<unsigned char>(id[i]);
        if (!std::isxdigit(a) || std::tolower(a) != std::tolower(b)) return false;
    }
    return true;
}

bool readLoad(const Options& opt, std::vector<uint8_t>& bytes, std::string& error)
{
    std::ifstream in(opt.load, std::ios::binary);
    if (!in) { error = "couldn't read " + opt.load.u8string(); return false; }
    bytes.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
    return true;
}

} // namespace mo

namespace {

constexpr const char* kVersion = "1";

std::string lower(std::string s)
{
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return s;
}

int run(const std::vector<std::filesystem::path>& args)
{
    mo::takeStdout();
    mo::Options opt;
    std::string format;
    std::vector<std::filesystem::path> paths;
    for (size_t i = 0; i < args.size(); i++)
    {
        auto a = args[i].u8string();
        if (a == "--version")
        {
            mo::Json j;
            mo::emit(j.begin().str("scanner", "mo-scan").str("version", kVersion).end());
            return 0;
        }
        if (a == "--no-params") opt.params = false;
        else if (a == "--state") opt.state = true;
        else if (a == "--curve" && i + 1 < args.size()) opt.curve = std::clamp(std::atoi(args[++i].u8string().c_str()), 1, 1000);
        else if (a == "--load" && i + 1 < args.size()) opt.load = args[++i];
        else if (a == "--load-program") opt.loadProgram = true;
        else if (a == "--only" && i + 1 < args.size()) opt.only = args[++i].u8string();
        else if (a == "--format" && i + 1 < args.size()) format = lower(args[++i].u8string());
        else paths.push_back(args[i]);
    }
    if (paths.empty())
    {
        std::fprintf(stderr, "usage: mo-scan [--no-params] [--state] [--curve <steps>] [--load <state file> [--load-program]] [--only <plugin id>] [--format clap|vst3|vst2] <plugin path>\n");
        return 64;
    }

    int failed = 0;
    for (auto& path : paths)
    {
        auto f = format.empty() ? lower(path.extension().u8string()) : "." + format;
        std::string error;
        bool ok;
        if (!std::filesystem::exists(path)) { error = "not found"; ok = false; }
        else if (f == ".clap") ok = mo::scanClap(path, opt, error);
        else if (f == ".vst3") ok = mo::scanVst3(path, opt, error);
        else if (f == ".dll" || f == ".so" || f == ".vst" || f == ".vst2") ok = mo::scanVst2(path, opt, error);
        else { error = "unknown plugin type"; ok = false; }
        if (!ok)
        {
            failed++;
            mo::Json j;
            mo::emit(j.begin().str("path", path.u8string()).str("error", error).end());
        }
    }
    return failed ? 2 : 0;
}

} // namespace

#ifdef _WIN32
int wmain(int argc, wchar_t** argv)
{
    // A plugin that crashes or can't find a DLL must not leave a Windows error box open: the
    // process should just end, so the app can note the failure and move on.
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
#ifdef _MSC_VER
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
    std::vector<std::filesystem::path> args(argv + 1, argv + argc);
    return run(args);
}
#else
int main(int argc, char** argv)
{
    std::vector<std::filesystem::path> args(argv + 1, argv + argc);
    return run(args);
}
#endif
