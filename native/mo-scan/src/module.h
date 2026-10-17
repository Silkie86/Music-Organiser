// The scanners for each format.
#pragma once

#include "library.h"

#include <filesystem>
#include <string>
#include <vector>

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
    /// A saved state to hand the plugin before anything is read (its VST2 bank chunk, or its VST3 component
    /// state). With it the state is printed back too, so another plugin's saved sound can be offered to one
    /// that says it can open it, and what it made of it read back (Serum 1's sound into Serum 2, say).
    std::filesystem::path load;
    /// Hand the VST2 chunk over as one program ("preset") rather than a whole bank.
    bool loadProgram = false;
    bool wanted(const std::string& id) const;
};

/// The file named by --load, or nothing (with an error) if it can't be read.
bool readLoad(const Options& opt, std::vector<uint8_t>& bytes, std::string& error);

/// Redirects the plugins' own stdout to stderr, keeping stdout for our JSON lines.
void takeStdout();

/// Each scanner writes one JSON line per plugin in the file and returns false if it couldn't load it at all.
bool scanClap(const std::filesystem::path& path, const Options& opt, std::string& error);
bool scanVst3(const std::filesystem::path& path, const Options& opt, std::string& error);
bool scanVst2(const std::filesystem::path& path, const Options& opt, std::string& error);

} // namespace mo
