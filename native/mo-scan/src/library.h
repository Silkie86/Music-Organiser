// Loading a plugin binary: a DLL, a .so, or a macOS bundle.
#pragma once

#include <filesystem>
#include <string>

namespace mo {

class Library
{
public:
    Library() = default;
    Library(const Library&) = delete;
    Library& operator=(const Library&) = delete;
    ~Library();

    /// A file, or on macOS a bundle folder.
    bool open(const std::filesystem::path& path, std::string& error);
    void* symbol(const char* name) const;
    void close();

    /// The CFBundleRef on macOS, for VST3's bundleEntry.
    void* bundle() const { return bundle_; }
    /// The dlopen handle on Linux, for VST3's ModuleEntry.
    void* handle() const { return handle_; }

private:
    void* handle_ = nullptr;
    void* bundle_ = nullptr;
};

} // namespace mo
