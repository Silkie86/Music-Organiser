#include "json.h"
#include "module.h"

#include <cstring>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#else
#include <fcntl.h>
#include <unistd.h>
#endif

namespace mo {

static FILE* g_out = stdout;

void takeStdout()
{
    // Plugins print all sorts to stdout. Keep the real stdout for our JSON and send theirs to stderr.
    std::fflush(stdout);
    // Helper processes a plugin starts mustn't inherit our stdout either: the app reads it to the end.
#ifdef _WIN32
    int saved = _dup(_fileno(stdout));
    _dup2(_fileno(stderr), _fileno(stdout));
    if (saved >= 0) SetHandleInformation(reinterpret_cast<HANDLE>(_get_osfhandle(saved)), HANDLE_FLAG_INHERIT, 0);
    g_out = _fdopen(saved, "w");
    _setmode(saved, _O_BINARY);
#else
    int saved = dup(fileno(stdout));
    dup2(fileno(stderr), fileno(stdout));
    if (saved >= 0) fcntl(saved, F_SETFD, FD_CLOEXEC);
    g_out = fdopen(saved, "w");
#endif
    if (!g_out) g_out = stderr;
}

void emit(const Json& j)
{
    std::fwrite(j.text().data(), 1, j.text().size(), g_out);
    std::fputc('\n', g_out);
    std::fflush(g_out);
}

std::string base64(const std::vector<uint8_t>& data)
{
    static const char* abc = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve((data.size() + 2) / 3 * 4);
    size_t i = 0;
    for (; i + 2 < data.size(); i += 3)
    {
        uint32_t n = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
        out += abc[n >> 18]; out += abc[(n >> 12) & 63]; out += abc[(n >> 6) & 63]; out += abc[n & 63];
    }
    if (i + 1 == data.size())
    {
        uint32_t n = data[i] << 16;
        out += abc[n >> 18]; out += abc[(n >> 12) & 63]; out += "==";
    }
    else if (i + 2 == data.size())
    {
        uint32_t n = (data[i] << 16) | (data[i + 1] << 8);
        out += abc[n >> 18]; out += abc[(n >> 12) & 63]; out += abc[(n >> 6) & 63]; out += '=';
    }
    return out;
}

std::string cleanUtf8(const char* s, size_t maxLen)
{
    std::string out;
    if (!s) return out;
    size_t len = strnlen(s, maxLen);
    for (size_t i = 0; i < len;)
    {
        auto c = static_cast<unsigned char>(s[i]);
        size_t n = c < 0x80 ? 1 : (c >> 5) == 6 ? 2 : (c >> 4) == 14 ? 3 : (c >> 3) == 30 ? 4 : 0;
        bool ok = n > 0 && i + n <= len;
        for (size_t k = 1; ok && k < n; k++) ok = (static_cast<unsigned char>(s[i + k]) & 0xC0) == 0x80;
        if (ok) { out.append(s + i, n); i += n; }
        else
        {
            // Old plugins use Windows-1252; treat a stray byte as Latin-1, which is close enough for names.
            out += static_cast<char>(0xC0 | (c >> 6));
            out += static_cast<char>(0x80 | (c & 0x3F));
            i++;
        }
    }
    return out;
}

std::string utf16ToUtf8(const char16_t* s, size_t maxLen)
{
    std::string out;
    for (size_t i = 0; i < maxLen && s[i]; i++)
    {
        uint32_t c = s[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < maxLen && s[i + 1] >= 0xDC00 && s[i + 1] < 0xE000)
            c = 0x10000 + ((c - 0xD800) << 10) + (s[++i] - 0xDC00);
        if (c < 0x80) out += static_cast<char>(c);
        else if (c < 0x800) { out += static_cast<char>(0xC0 | (c >> 6)); out += static_cast<char>(0x80 | (c & 0x3F)); }
        else if (c < 0x10000)
        {
            out += static_cast<char>(0xE0 | (c >> 12));
            out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (c & 0x3F));
        }
        else
        {
            out += static_cast<char>(0xF0 | (c >> 18));
            out += static_cast<char>(0x80 | ((c >> 12) & 0x3F));
            out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (c & 0x3F));
        }
    }
    return out;
}

} // namespace mo
