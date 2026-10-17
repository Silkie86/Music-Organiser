#include "live.h"

#include <algorithm>
#include <chrono>
#include <cstring>
#include <fstream>
#include <locale>
#include <sstream>
#include <vector>

namespace probe {

namespace fs = std::filesystem;
using namespace std::chrono_literals;

// ---- Reading commands ----

namespace {

void putUtf8(std::string& out, unsigned cp)
{
    if (cp >= 0xD800 && cp < 0xE000) cp = 0xFFFD;   // half a surrogate pair
    if (cp < 0x80) out += static_cast<char>(cp);
    else if (cp < 0x800) { out += static_cast<char>(0xC0 | cp >> 6); out += static_cast<char>(0x80 | (cp & 0x3F)); }
    else if (cp < 0x10000)
    {
        out += static_cast<char>(0xE0 | cp >> 12);
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
    else
    {
        out += static_cast<char>(0xF0 | cp >> 18);
        out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

/// A command file's text as UTF-8: without a byte order mark, and UTF-16 (what Windows PowerShell's
/// ">" writes) turned into UTF-8.
std::string fileText(const std::string& bytes)
{
    auto at = [&](size_t i) { return static_cast<unsigned char>(bytes[i]); };
    if (bytes.size() >= 3 && at(0) == 0xEF && at(1) == 0xBB && at(2) == 0xBF) return bytes.substr(3);
    bool little = bytes.size() >= 2 && at(0) == 0xFF && at(1) == 0xFE;
    bool big = bytes.size() >= 2 && at(0) == 0xFE && at(1) == 0xFF;
    if (!little && !big) return bytes;
    auto unit = [&](size_t i) { return little ? at(i) | at(i + 1) << 8 : at(i) << 8 | at(i + 1); };
    std::string out;
    for (size_t i = 2; i + 1 < bytes.size(); i += 2)
    {
        unsigned cp = unit(i);
        if (cp >= 0xD800 && cp < 0xDC00 && i + 3 < bytes.size())
        {
            unsigned low = unit(i + 2);
            if (low >= 0xDC00 && low < 0xE000) { cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00); i += 2; }
        }
        putUtf8(out, cp);
    }
    return out;
}

/// The text with anything that isn't UTF-8 (e.g. a file saved in the Windows code page) as "?", so
/// the report and the status stay readable.
std::string validUtf8(const std::string& s)
{
    static const unsigned kLowest[] = { 0, 0, 0x80, 0x800, 0x10000 };
    std::string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size();)
    {
        unsigned char c = static_cast<unsigned char>(s[i]);
        unsigned n = c < 0x80 ? 1 : (c >> 5) == 0x6 ? 2 : (c >> 4) == 0xE ? 3 : (c >> 3) == 0x1E ? 4 : 0;
        bool ok = n && i + n <= s.size();
        unsigned cp = c & (0x7F >> n);
        for (unsigned k = 1; ok && k < n; k++)
        {
            ok = (s[i + k] & 0xC0) == 0x80;
            cp = cp << 6 | (s[i + k] & 0x3F);
        }
        if (ok && n > 1) ok = cp >= kLowest[n] && cp <= 0x10FFFF && (cp < 0xD800 || cp >= 0xE000);
        if (ok) { out.append(s, i, n); i += n; }
        else { out += '?'; i++; }
    }
    return out;
}

/// Just enough JSON for commands.
struct Parser
{
    const char* p;
    const char* end;

    void space()
    {
        while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++;
    }

    bool eat(char c)
    {
        space();
        if (p >= end || *p != c) return false;
        p++;
        return true;
    }

    bool word(const char* w)
    {
        size_t n = std::strlen(w);
        if (static_cast<size_t>(end - p) < n || std::strncmp(p, w, n)) return false;
        p += n;
        return true;
    }

    bool hex4(unsigned& out)
    {
        if (end - p < 4) return false;
        out = 0;
        for (int i = 0; i < 4; i++, p++)
        {
            char c = *p;
            unsigned d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 16;
            if (d > 15) return false;
            out = out << 4 | d;
        }
        return true;
    }

    bool quoted(std::string& out)
    {
        if (!eat('"')) return false;
        while (p < end)
        {
            unsigned char c = static_cast<unsigned char>(*p++);
            if (c == '"') return true;
            if (c < 0x20) return false;
            if (c != '\\') { out += static_cast<char>(c); continue; }
            if (p >= end) return false;
            switch (*p++)
            {
            case '"': out += '"'; break;
            case '\\': out += '\\'; break;
            case '/': out += '/'; break;
            case 'b': out += '\b'; break;
            case 'f': out += '\f'; break;
            case 'n': out += '\n'; break;
            case 'r': out += '\r'; break;
            case 't': out += '\t'; break;
            case 'u':
            {
                unsigned cp;
                if (!hex4(cp)) return false;
                if (cp >= 0xD800 && cp < 0xDC00 && end - p >= 6 && p[0] == '\\' && p[1] == 'u')
                {
                    const char* back = p;
                    unsigned low;
                    p += 2;
                    if (hex4(low) && low >= 0xDC00 && low < 0xE000) cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                    else p = back;
                }
                putUtf8(out, cp);
                break;
            }
            default: return false;
            }
        }
        return false;
    }

    bool number(double& out)
    {
        const char* start = p;
        while (p < end && ((*p >= '0' && *p <= '9') || *p == '-' || *p == '+' || *p == '.' || *p == 'e' || *p == 'E')) p++;
        if (p == start) return false;
        // In the C locale whatever the DAW has set, so 0.5 is always a half.
        std::istringstream in(std::string(start, p));
        in.imbue(std::locale::classic());
        in >> out;
        return !in.fail() && in.eof();
    }

    bool value(Field& f, int depth)
    {
        space();
        if (p >= end) return false;
        switch (*p)
        {
        case '"': f.kind = Field::String; return quoted(f.text);
        case '{':
        case '[': f.kind = Field::Other; return skip(depth);
        case 't': f.kind = Field::Bool; f.flag = true; return word("true");
        case 'f': f.kind = Field::Bool; f.flag = false; return word("false");
        case 'n': f.kind = Field::Null; return word("null");
        default: f.kind = Field::Number; return number(f.number);
        }
    }

    /// An object or array inside a command, which no command uses: checked, then forgotten.
    bool skip(int depth)
    {
        if (depth > 32) return false;
        char close = *p++ == '{' ? '}' : ']';
        if (eat(close)) return true;
        for (;;)
        {
            std::string key;
            Field f;
            if (close == '}' && (!quoted(key) || !eat(':'))) return false;
            if (!value(f, depth + 1)) return false;
            if (eat(',')) continue;
            return eat(close);
        }
    }
};

} // namespace

bool parseObject(const std::string& text, Fields& out)
{
    Parser ps{ text.data(), text.data() + text.size() };
    if (!ps.eat('{')) return false;
    if (!ps.eat('}'))
        for (;;)
        {
            std::string key;
            Field f;
            if (!ps.quoted(key) || !ps.eat(':') || !ps.value(f, 1)) return false;
            out[key] = std::move(f);
            if (ps.eat(',')) continue;
            if (ps.eat('}')) break;
            return false;
        }
    ps.space();
    return ps.p == ps.end;
}

// ---- Live ----

Live::Live(std::string instance, Status status, Command command)
    : instance_(std::move(instance)), status_(std::move(status)), command_(std::move(command))
{
}

Live::~Live()
{
    {
        std::lock_guard<std::mutex> lock(mutex_);
        stop_ = true;
    }
    wake_.notify_all();
    if (thread_.joinable()) thread_.join();
    if (folder_.empty()) return;
    std::error_code ec;
    fs::remove(statusFile_, ec);
    fs::remove(fs::path(statusFile_) += ".tmp", ec);
    fs::remove_all(inbox_, ec);
}

void Live::start()
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (started_ || stop_) return;
    started_ = true;
    try
    {
        thread_ = std::thread([this] { run(); });
    }
    catch (...)
    {
        // No thread, no live link; the plugin itself carries on.
    }
}

void Live::run()
{
    // Nothing here may throw out of the thread: that would take the DAW down with it.
    using clock = std::chrono::steady_clock;
    try
    {
        folder_ = reportFolder() / "live";
        statusFile_ = folder_ / (instance_ + ".json");
        inbox_ = folder_ / (instance_ + ".in");
        std::error_code ec;
        fs::create_directories(folder_, ec);
        clearStale();
    }
    catch (...)
    {
        // Carry on: without a status file name, writeStatus does nothing.
    }

    auto lastWrite = clock::now() - 1s;
    bool due = true;
    for (;;)
    {
        try
        {
            // Straight after commands, so a ping or a knob change shows at once. When the status
            // couldn't be replaced (a reader had it open), the next pass, 100 ms on, tries again.
            if (readCommands()) due = true;
            if (due || clock::now() - lastWrite >= 500ms)
            {
                due = !writeStatus();
                if (!due) lastWrite = clock::now();
            }
        }
        catch (...)
        {
            // Try again next time round.
        }
        std::unique_lock<std::mutex> lock(mutex_);
        if (wake_.wait_for(lock, 100ms, [this] { return stop_; })) return;
    }
}

/// Removes statuses (and command folders) over a minute old: left by a DAW that went away without
/// saying so.
void Live::clearStale()
{
    std::error_code ec;
    auto now = fs::file_time_type::clock::now();
    std::vector<fs::path> gone;
    for (fs::directory_iterator it(folder_, ec), end; !ec && it != end; it.increment(ec))
    {
        std::error_code e;
        auto changed = it->last_write_time(e);
        if (e || now - changed < 60s) continue;
        auto path = it->path();
        auto ext = path.extension();
        if (ext == ".json")
        {
            gone.push_back(path);
            gone.push_back(fs::path(path).replace_extension(".in"));
        }
        else if (ext == ".tmp") gone.push_back(path);
        else if (ext == ".in" && !fs::exists(fs::path(path).replace_extension(".json"), e)) gone.push_back(path);
    }
    for (auto& path : gone) fs::remove_all(path, ec);
}

/// Handles the command files in name order. True if there were any.
bool Live::readCommands()
{
    std::error_code ec;
    auto now = fs::file_time_type::clock::now();
    std::vector<fs::path> files;
    for (fs::directory_iterator it(inbox_, ec), end; !ec && it != end; it.increment(ec))
    {
        std::error_code e;
        if (it->path().extension() != ".json" || !it->is_regular_file(e)) continue;
        // Changed in the last 150 ms: it may still be being written.
        auto changed = it->last_write_time(e);
        if (e || now - changed < 150ms) continue;
        files.push_back(it->path());
    }
    std::sort(files.begin(), files.end());

    bool any = false;
    for (auto& file : files)
    {
        std::string bytes;
        {
            std::ifstream in(file, std::ios::binary);
            if (!in) continue;
            std::ostringstream text;
            text << in.rdbuf();
            bytes = text.str();
        }
        // Windows won't delete a file someone still has open; then it's next time, so a command is
        // never handled twice.
        if (!fs::remove(file, ec) || ec) continue;
        any = true;
        std::string text = fileText(bytes), name = file.filename().u8string();
        for (size_t at = 0; at < text.size();)
        {
            size_t next = text.find('\n', at);
            if (next == std::string::npos) next = text.size();
            size_t first = at, last = next;
            auto blank = [&](size_t i) { return text[i] == ' ' || text[i] == '\t' || text[i] == '\r'; };
            while (first < last && blank(first)) first++;
            while (last > first && blank(last - 1)) last--;
            if (last > first) command_(validUtf8(text.substr(first, last - first)), name);
            at = next + 1;
        }
    }
    return any;
}

/// Writes the status to a temporary file and renames it over the old one, so a reader never sees
/// half of it. While a reader has it open Windows won't replace it; then it's next time.
bool Live::writeStatus()
{
    if (statusFile_.empty()) return true;
    std::vector<char> buf(16384);
    Line line(buf.data(), buf.size());
    status_(line);
    std::string text = line.finish();
    text += '\n';

    auto tmp = fs::path(statusFile_) += ".tmp";
    {
        std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
        if (!out) return false;
        out.write(text.data(), static_cast<std::streamsize>(text.size()));
        if (!out.flush()) return false;
    }
    // The command folder first, so it's there as soon as anyone can see the status.
    std::error_code ec;
    fs::create_directories(inbox_, ec);
    fs::rename(tmp, statusFile_, ec);
    return !ec;
}

} // namespace probe
