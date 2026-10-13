// The scanners for each format.
#pragma once

#include "library.h"

#include <filesystem>
#include <string>

namespace mo {

struct Options
{
    bool params = true;
    bool state = false;
};

/// Redirects the plugins' own stdout to stderr, keeping stdout for our JSON lines.
void takeStdout();

/// Each scanner writes one JSON line per plugin in the file and returns false if it couldn't load it at all.
bool scanClap(const std::filesystem::path& path, const Options& opt, std::string& error);
bool scanVst3(const std::filesystem::path& path, const Options& opt, std::string& error);
bool scanVst2(const std::filesystem::path& path, const Options& opt, std::string& error);

} // namespace mo
