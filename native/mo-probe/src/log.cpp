#include "log.h"

#include <chrono>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <random>
#include <algorithm>

namespace probe {

// ---- Line ----

static void formatNow(char* out, size_t size)
{
    using namespace std::chrono;
    auto now = system_clock::now();
    auto ms = duration_cast<milliseconds>(now.time_since_epoch()).count() % 1000;
    std::time_t t = system_clock::to_time_t(now);
    std::tm tm{};
#ifdef _WIN32
    gmtime_s(&tm, &t);
#else
    gmtime_r(&t, &tm);
#endif
    std::snprintf(out, size, "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ", tm.tm_year + 1900, tm.tm_mon + 1, tm.tm_mday,
                  tm.tm_hour, tm.tm_min, tm.tm_sec, static_cast<int>(ms));
}

std::string nowIso()
{
    char buf[64];
    formatNow(buf, sizeof buf);
    return buf;
}

Line::Line(char* buf, size_t size, const char* event) : buf_(buf), size_(size)
{
    char t[64];
    formatNow(t, sizeof t);
    putc('{');
    str("t", t);
    str("event", event);
}

void Line::put(const char* s)
{
    size_t n = std::strlen(s);
    if (truncated_ || len_ + n + 2 > size_) { truncated_ = true; return; }   // keep room for "}" and the terminator
    std::memcpy(buf_ + len_, s, n);
    len_ += n;
}

void Line::putc(char c)
{
    char s[2] = { c, 0 };
    put(s);
}

void Line::key(const char* k)
{
    if (len_ > 1) putc(',');
    putc('"');
    put(k);
    put("\":");
}

Line& Line::str(const char* k, const char* value)
{
    if (truncated_) return *this;
    size_t start = len_;
    key(k);
    putc('"');
    for (const unsigned char* p = reinterpret_cast<const unsigned char*>(value ? value : ""); *p; p++)
    {
        char esc[8];
        switch (*p)
        {
        case '"': put("\\\""); break;
        case '\\': put("\\\\"); break;
        case '\n': put("\\n"); break;
        case '\r': put("\\r"); break;
        case '\t': put("\\t"); break;
        default:
            if (*p < 0x20) { std::snprintf(esc, sizeof esc, "\\u%04x", *p); put(esc); }
            else putc(static_cast<char>(*p));
        }
    }
    putc('"');
    if (truncated_) len_ = start;
    return *this;
}

Line& Line::num(const char* k, double value)
{
    if (truncated_) return *this;
    size_t start = len_;
    key(k);
    char buf[40];
    if (std::isfinite(value)) std::snprintf(buf, sizeof buf, "%.10g", value);
    else std::snprintf(buf, sizeof buf, "null");
    put(buf);
    if (truncated_) len_ = start;
    return *this;
}

Line& Line::integer(const char* k, long long value)
{
    if (truncated_) return *this;
    size_t start = len_;
    key(k);
    char buf[32];
    std::snprintf(buf, sizeof buf, "%lld", value);
    put(buf);
    if (truncated_) len_ = start;
    return *this;
}

Line& Line::boolean(const char* k, bool value)
{
    if (truncated_) return *this;
    size_t start = len_;
    key(k);
    put(value ? "true" : "false");
    if (truncated_) len_ = start;
    return *this;
}

Line& Line::raw(const char* k, const char* json)
{
    if (truncated_) return *this;
    size_t start = len_;
    key(k);
    put(json);
    if (truncated_) len_ = start;
    return *this;
}

const char* Line::finish()
{
    // put() always leaves room for these two.
    buf_[len_++] = '}';
    buf_[len_] = 0;
    return buf_;
}

// ---- Log ----

std::filesystem::path reportFolder()
{
    std::filesystem::path base;
#if defined(_WIN32)
    if (const wchar_t* local = _wgetenv(L"LOCALAPPDATA")) base = local;
#elif defined(__APPLE__)
    if (const char* home = std::getenv("HOME")) base = std::filesystem::path(home) / "Library" / "Application Support";
#else
    if (const char* xdg = std::getenv("XDG_DATA_HOME"); xdg && *xdg) base = xdg;
    else if (const char* home = std::getenv("HOME")) base = std::filesystem::path(home) / ".local" / "share";
#endif
    std::error_code ec;
    if (base.empty()) base = std::filesystem::temp_directory_path(ec);
    auto folder = base / "MusicOrganiser" / "probe";
    std::filesystem::create_directories(folder, ec);
    return folder;
}

std::string randomId()
{
    std::random_device rd;
    std::mt19937_64 rng((static_cast<uint64_t>(rd()) << 32) ^ rd()
                        ^ static_cast<uint64_t>(std::chrono::high_resolution_clock::now().time_since_epoch().count()));
    char buf[20];
    std::snprintf(buf, sizeof buf, "%016llX", static_cast<unsigned long long>(rng()));
    return buf;
}

Log::Log(std::string kind, std::string instance) : kind_(std::move(kind)), instance_(std::move(instance))
{
    ring_.resize(kSlots);
    writer_ = std::thread([this] { run(); });
}

Log::~Log()
{
    {
        std::lock_guard<std::mutex> lock(mutex_);
        stop_ = true;
    }
    wake_.notify_all();
    if (writer_.joinable()) writer_.join();
    if (out_) std::fclose(out_);
}

void Log::write(const char* line)
{
    std::lock_guard<std::mutex> lock(mutex_);
    mainQueue_.emplace_back(line);
}

bool Log::writeAudio(const char* line)
{
    size_t h = head_.load(std::memory_order_relaxed);
    size_t t = tail_.load(std::memory_order_acquire);
    if (h - t >= kSlots)
    {
        dropped_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    auto& slot = ring_[h % kSlots];
    std::strncpy(slot.text, line, kSlotSize - 1);
    slot.text[kSlotSize - 1] = 0;
    head_.store(h + 1, std::memory_order_release);
    return true;
}

void Log::keep()
{
    {
        std::lock_guard<std::mutex> lock(mutex_);
        keep_ = true;
    }
    wake_.notify_all();
}

void Log::open()
{
    char stamp[32];
    {
        std::time_t t = std::time(nullptr);
        std::tm tm{};
#ifdef _WIN32
        gmtime_s(&tm, &t);
#else
        gmtime_r(&t, &tm);
#endif
        std::strftime(stamp, sizeof stamp, "%Y%m%dT%H%M%SZ", &tm);
    }
    path_ = reportFolder() / (std::string(stamp) + "-" + kind_ + "-" + instance_.substr(0, 8) + ".jsonl");
#ifdef _WIN32
    out_ = _wfopen(path_.c_str(), L"ab");
#else
    out_ = std::fopen(path_.c_str(), "ab");
#endif
}

void Log::drain(std::vector<std::string>& lines)
{
    size_t t = tail_.load(std::memory_order_relaxed);
    size_t h = head_.load(std::memory_order_acquire);
    for (; t != h; t++) lines.emplace_back(ring_[t % kSlots].text);
    tail_.store(t, std::memory_order_release);
    if (unsigned d = dropped_.exchange(0))
    {
        char buf[160];
        Line l(buf, sizeof buf, "dropped");
        l.integer("count", d);
        lines.emplace_back(l.finish());
    }
}

/// The "n" a line was numbered with, or 0 for lines without one.
static unsigned long long sequence(const std::string& line)
{
    auto at = line.find("\"n\":");
    return at == std::string::npos ? 0 : std::strtoull(line.c_str() + at + 4, nullptr, 10);
}

void Log::run()
{
    std::vector<std::string> lines;
    for (;;)
    {
        bool stopping, keeping;
        {
            std::unique_lock<std::mutex> lock(mutex_);
            wake_.wait_for(lock, std::chrono::milliseconds(50), [this] { return stop_; });
            stopping = stop_;
            keeping = keep_;
            lines.swap(mainQueue_);
        }
        drain(lines);
        // Lines from the main and audio threads arrive separately; put them back in order.
        std::stable_sort(lines.begin(), lines.end(), [](const std::string& a, const std::string& b) { return sequence(a) < sequence(b); });

        if (keeping && !out_)
        {
            open();
            if (out_)
                for (auto& p : pending_) { std::fputs(p.c_str(), out_); std::fputc('\n', out_); }
            pending_.clear();
        }
        if (out_)
        {
            for (auto& l : lines) { std::fputs(l.c_str(), out_); std::fputc('\n', out_); }
            if (!lines.empty()) std::fflush(out_);
        }
        else
        {
            pending_.insert(pending_.end(), lines.begin(), lines.end());
            if (pending_.size() > 2000) pending_.erase(pending_.begin(), pending_.begin() + (pending_.size() - 2000));
        }
        lines.clear();
        if (stopping) return;
    }
}

} // namespace probe
