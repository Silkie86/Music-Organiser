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
    /// With a number: each parameter's text (and, for VST3, its value in real units) at that many even steps
    /// from 0 to 1, for matching one plugin's knobs to another's.
    int curve = 0;
    /// Only this plugin from the file (its ID as printed), e.g. one plugin from a Waves shell.
    std::string only;
    bool wanted(const std::string& id) const;
};

/// Redirects the plugins' own stdout to stderr, keeping stdout for our JSON lines.
void takeStdout();

/// Each scanner writes one JSON line per plugin in the file and returns false if it couldn't load it at all.
bool scanClap(const std::filesystem::path& path, const Options& opt, std::string& error);
bool scanVst3(const std::filesystem::path& path, const Options& opt, std::string& error);
bool scanVst2(const std::filesystem::path& path, const Options& opt, std::string& error);

} // namespace mo
