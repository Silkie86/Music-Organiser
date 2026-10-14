// The probe's report: JSON lines written to a file the Music Organiser app reads.
//
// One file per plugin instance, in the app's data folder:
//   Windows  %LOCALAPPDATA%\MusicOrganiser\probe
//   macOS    ~/Library/Application Support/MusicOrganiser/probe
//   Linux    ~/.local/share/MusicOrganiser/probe
//
// The file is only created once the instance does something real (activates, saves or loads its
// state, or learns its track), so a DAW scanning its plugins doesn't leave empty reports behind.
#pragma once

#include <array>
#include <atomic>
#include <condition_variable>
#include <cstdio>
#include <filesystem>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace probe {

/// Builds one JSON object as text. Small and allocation-free once constructed, so the audio
/// thread can use it on a stack buffer.
class Line
{
public:
    Line(char* buf, size_t size, const char* event);
    /// A plain object, without the time and event a report line starts with.
    Line(char* buf, size_t size);
    Line& str(const char* key, const char* value);
    Line& str(const char* key, const std::string& value) { return str(key, value.c_str()); }
    Line& num(const char* key, double value);
    Line& integer(const char* key, long long value);
    Line& boolean(const char* key, bool value);
    /// Raw JSON, e.g. a nested object or array.
    Line& raw(const char* key, const char* json);
    const char* finish();
    bool truncated() const { return truncated_; }

private:
    char* buf_;
    size_t size_;
    size_t len_ = 0;
    bool truncated_ = false;
    void put(const char* s);
    void putc(char c);
    void key(const char* k);
};

class Log
{
public:
    Log(std::string kind, std::string instance);
    ~Log();
    Log(const Log&) = delete;
    Log& operator=(const Log&) = delete;

    /// From the main thread (or any thread that may block briefly).
    void write(const char* line);
    /// From the audio thread: never blocks or allocates. Returns false if the buffer was full.
    bool writeAudio(const char* line);
    /// The instance is in real use: create the report file and keep writing to it.
    void keep();
    /// The report file's name (no folder), or empty before keep(). [any thread]
    std::string fileName() const;

private:
    static constexpr size_t kSlots = 4096;
    static constexpr size_t kSlotSize = 512;
    struct Slot { char text[kSlotSize]; };

    std::string kind_;
    std::string instance_;
    std::string name_;   // the report file's name, chosen by keep()

    mutable std::mutex mutex_;
    std::condition_variable wake_;
    std::vector<std::string> mainQueue_;
    std::vector<std::string> pending_;   // lines kept until the file exists
    bool keep_ = false;
    bool stop_ = false;

    std::vector<Slot> ring_;
    std::atomic<size_t> head_{ 0 };   // written by the audio thread
    std::atomic<size_t> tail_{ 0 };   // written by the writer thread
    std::atomic<unsigned> dropped_{ 0 };

    FILE* out_ = nullptr;
    std::thread writer_;

    void run();
    void drain(std::vector<std::string>& lines);
    void open(const std::string& name);
};

/// The folder the reports go in, created if missing.
std::filesystem::path reportFolder();

/// Current time as ISO 8601 UTC with milliseconds.
std::string nowIso();

/// 16 random hex digits.
std::string randomId();

} // namespace probe
