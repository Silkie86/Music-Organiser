#include "library.h"

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#endif

#ifdef __APPLE__
#include <CoreFoundation/CoreFoundation.h>
#endif

namespace mo {

Library::~Library() { close(); }

bool Library::open(const std::filesystem::path& path, std::string& error)
{
#ifdef __APPLE__
    if (std::filesystem::is_directory(path))
    {
        auto s = path.string();
        CFURLRef url = CFURLCreateFromFileSystemRepresentation(nullptr, reinterpret_cast<const UInt8*>(s.c_str()), s.size(), true);
        bundle_ = url ? CFBundleCreate(nullptr, url) : nullptr;
        if (url) CFRelease(url);
        CFErrorRef err = nullptr;
        if (!bundle_ || !CFBundleLoadExecutableAndReturnError(static_cast<CFBundleRef>(bundle_), &err))
        {
            error = "couldn't load the bundle";
            if (err) CFRelease(err);
            return false;
        }
        return true;
    }
#endif
#ifdef _WIN32
    // Let the plugin find DLLs that sit next to it.
    handle_ = LoadLibraryExW(path.wstring().c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!handle_)
    {
        error = "LoadLibrary failed (error " + std::to_string(GetLastError()) + ")";
        return false;
    }
#else
    handle_ = dlopen(path.string().c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!handle_)
    {
        const char* e = dlerror();
        error = e ? e : "dlopen failed";
        return false;
    }
#endif
    return true;
}

void* Library::symbol(const char* name) const
{
#ifdef __APPLE__
    if (bundle_)
    {
        CFStringRef s = CFStringCreateWithCString(nullptr, name, kCFStringEncodingUTF8);
        void* p = CFBundleGetFunctionPointerForName(static_cast<CFBundleRef>(bundle_), s);
        CFRelease(s);
        return p;
    }
#endif
    if (!handle_) return nullptr;
#ifdef _WIN32
    return reinterpret_cast<void*>(GetProcAddress(static_cast<HMODULE>(handle_), name));
#else
    return dlsym(handle_, name);
#endif
}

void Library::close()
{
    // Many plugins crash or hang when unloaded, and the process ends right after anyway,
    // so the library is deliberately left loaded.
    handle_ = nullptr;
    bundle_ = nullptr;
}

} // namespace mo
