// The probe's live link: a status file anything can read, and a folder anything can drop commands in.
// Plain files, so the app, Bitwig's script and Claude (through file tools) can all take part.
//
//   <report folder>/live/<instance>.json       the status, rewritten about every 0.5 s
//   <report folder>/live/<instance>.in/*.json  commands, one JSON object per line, read about every 100 ms
//
// Nothing is written until start() (the instance is in real use), and both go when the Live is destroyed.
// See native/README.md, "Talking to it while it runs", for what's in them.
#pragma once

#include "log.h"

#include <condition_variable>
#include <filesystem>
#include <functional>
#include <map>
#include <mutex>
#include <string>
#include <thread>

namespace probe {

/// One field of a command: a JSON string, number, true/false or null. Nested objects and arrays
/// are allowed but not kept (kind Other).
struct Field
{
    enum Kind { String, Number, Bool, Null, Other } kind = Null;
    std::string text;
    double number = 0;
    bool flag = false;
};
using Fields = std::map<std::string, Field>;

/// Reads one JSON object into its fields; false if the text isn't one.
bool parseObject(const std::string& text, Fields& out);

class Live
{
public:
    /// Fills in the status object. Called on the live thread.
    using Status = std::function<void(Line&)>;
    /// Handles one line of a command file (any text; it may not even be JSON). Called on the live thread.
    using Command = std::function<void(const std::string& line, const std::string& file)>;

    Live(std::string instance, Status status, Command command);
    /// Stops the live thread, then deletes the status file and the command folder.
    ~Live();
    Live(const Live&) = delete;
    Live& operator=(const Live&) = delete;

    /// The instance is in real use: start writing the status and reading commands. [main thread]
    void start();

private:
    std::string instance_;
    Status status_;
    Command command_;
    std::filesystem::path folder_, statusFile_, inbox_;   // set by the live thread

    std::mutex mutex_;
    std::condition_variable wake_;
    bool started_ = false;
    bool stop_ = false;
    std::thread thread_;

    void run();
    void clearStale();
    bool readCommands();
    void writeStatus();
};

} // namespace probe
