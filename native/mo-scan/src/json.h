// A tiny JSON writer: one object per line, which is what the app reads back.
#pragma once

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace mo {

class Json
{
public:
    Json& begin() { open('{'); return *this; }
    Json& end() { close('}'); return *this; }
    Json& beginArray(const char* key) { name(key); open('['); return *this; }
    Json& endArray() { close(']'); return *this; }
    /// An object inside an array.
    Json& item() { comma(); out_ += '{'; first_ = true; return *this; }

    Json& str(const char* key, const std::string& value) { name(key); quote(value); return *this; }
    Json& num(const char* key, double value)
    {
        name(key);
        char buf[64];
        if (std::isfinite(value)) std::snprintf(buf, sizeof buf, "%.17g", value);
        else std::snprintf(buf, sizeof buf, "null");
        out_ += buf;
        first_ = false;
        return *this;
    }
    Json& integer(const char* key, int64_t value) { name(key); out_ += std::to_string(value); first_ = false; return *this; }
    Json& boolean(const char* key, bool value) { name(key); out_ += value ? "true" : "false"; first_ = false; return *this; }
    Json& strings(const char* key, const std::vector<std::string>& values)
    {
        beginArray(key);
        for (auto& v : values) quote(v);
        return endArray();
    }

    const std::string& text() const { return out_; }

private:
    std::string out_;
    bool first_ = true;

    void open(char c) { out_ += c; first_ = true; }
    void close(char c) { out_ += c; first_ = false; }
    void comma()
    {
        if (!first_) out_ += ',';
        first_ = false;
    }
    void name(const char* key)
    {
        quote(key);
        out_ += ':';
        first_ = true;   // the value that follows needs no comma
    }
    void quote(const std::string& s)
    {
        comma();
        out_ += '"';
        for (unsigned char c : s)
        {
            switch (c)
            {
            case '"': out_ += "\\\""; break;
            case '\\': out_ += "\\\\"; break;
            case '\n': out_ += "\\n"; break;
            case '\r': out_ += "\\r"; break;
            case '\t': out_ += "\\t"; break;
            default:
                if (c < 0x20)
                {
                    char buf[8];
                    std::snprintf(buf, sizeof buf, "\\u%04x", c);
                    out_ += buf;
                }
                else out_ += static_cast<char>(c);
            }
        }
        out_ += '"';
    }
};

/// Writes one finished line to the real standard output (plugins' own printing goes to stderr).
void emit(const Json& j);

std::string base64(const std::vector<uint8_t>& data);

/// Text from a plugin can be anything; keep only valid UTF-8 so the JSON stays readable.
std::string cleanUtf8(const char* s, size_t maxLen);

std::string utf16ToUtf8(const char16_t* s, size_t maxLen);

} // namespace mo
