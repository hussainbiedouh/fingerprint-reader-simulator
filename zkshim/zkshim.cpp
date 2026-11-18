// x64 fake libzkfp.dll. Implements the ZKFPM_* exports used by MER's
// ctypes binding (E:/Projects/mer/ZktecoZkfReader.py:106-170).
#include <windows.h>

#include <cstring>
#include <cwchar>
#include <mutex>
#include <utility>
#include <string>
#include <unordered_map>
#include <vector>

namespace {

constexpr BYTE kMagic[4] = {'M', 'Z', 'K', 'F'};
constexpr UINT kHeaderSize = 8;
constexpr UINT kMaxTemplate = 2048;
constexpr UINT kImageSize = 65536;  // 256 x 256
constexpr UINT kDeviceHandle = 1;

constexpr int kOk = 0;
constexpr int kErrParam = -5;
constexpr int kErrHandle = -7;
constexpr int kErrCapture = -8;
constexpr int kErrMemory = -11;
constexpr int kErrNoMatch = -20;
constexpr int kErrMerge = -22;

std::mutex g_dbMutex;
std::mutex g_pipeMutex;

// dbHandle -> (fid -> opaque template payload). Scoped per DB, dropped on DBFree.
std::unordered_map<ULONG_PTR, std::unordered_map<UINT, std::vector<BYTE>>> g_dbs;
ULONG_PTR g_nextDbHandle = 0x1000;

UINT ReadLength(const BYTE* frame) {
    return static_cast<UINT>(frame[4]) |
           (static_cast<UINT>(frame[5]) << 8) |
           (static_cast<UINT>(frame[6]) << 16) |
           (static_cast<UINT>(frame[7]) << 24);
}

// One record: magic(4) + LE uint32 payload length(4) + payload.
bool ParseFrame(const BYTE* data, UINT cb, UINT& frameSize) {
    if (!data || cb < kHeaderSize) return false;
    if (std::memcmp(data, kMagic, 4) != 0) return false;
    const UINT len = ReadLength(data);
    if (len == 0 || len > kMaxTemplate - kHeaderSize) return false;
    frameSize = kHeaderSize + len;
    return frameSize <= cb;
}

// Accepts a single frame or a concatenation of frames (merged template).
bool IsValidTemplate(const BYTE* data, UINT cb) {
    if (!data || cb == 0 || cb > kMaxTemplate) return false;
    UINT offset = 0;
    while (offset < cb) {
        UINT frameSize = 0;
        if (!ParseFrame(data + offset, cb - offset, frameSize)) return false;
        offset += frameSize;
    }
    return offset == cb;
}

// Match if any frame of a equals any frame of b (frame-boundary containment).
bool TemplatesMatch(const BYTE* a, UINT ca, const BYTE* b, UINT cb) {
    if (!IsValidTemplate(a, ca) || !IsValidTemplate(b, cb)) return false;
    UINT oa = 0;
    while (oa < ca) {
        UINT sa = 0;
        ParseFrame(a + oa, ca - oa, sa);
        UINT ob = 0;
        while (ob < cb) {
            UINT sb = 0;
            ParseFrame(b + ob, cb - ob, sb);
            if (sa == sb && std::memcmp(a + oa, b + ob, sa) == 0) return true;
            ob += sb;
        }
        oa += sa;
    }
    return false;
}

int HexDigit(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

bool Base64Decode(const std::string& in, std::vector<BYTE>& out) {
    out.clear();
    if (in.empty() || (in.size() % 4) != 0) return false;
    auto valueOf = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '+') return 62;
        if (c == '/') return 63;
        return -1;
    };
    out.reserve((in.size() / 4) * 3);
    for (size_t i = 0; i < in.size(); i += 4) {
        int v[4] = {0, 0, 0, 0};
        for (int j = 0; j < 4; ++j) {
            const char c = in[i + j];
            if (c == '=') {
                if (i + 4 != in.size() || j < 2) return false;
            } else {
                v[j] = valueOf(c);
                if (v[j] < 0) return false;
            }
        }
        out.push_back(static_cast<BYTE>((v[0] << 2) | (v[1] >> 4)));
        if (in[i + 2] != '=') out.push_back(static_cast<BYTE>(((v[1] & 0x0F) << 4) | (v[2] >> 2)));
        if (in[i + 3] != '=') out.push_back(static_cast<BYTE>(((v[2] & 0x03) << 6) | v[3]));
    }
    return !out.empty();
}

struct Acquisition {
    std::vector<BYTE> payload;
};

// Line format: "ACQUIRE <idHex> <base64Template>".
bool ParseAcquireLine(const std::string& line, Acquisition& out) {
    const std::string prefix = "ACQUIRE ";
    if (line.compare(0, prefix.size(), prefix) != 0) return false;
    const size_t idStart = line.find_first_not_of(" \t", prefix.size());
    if (idStart == std::string::npos) return false;
    const size_t idEnd = line.find_first_of(" \t", idStart);
    if (idEnd == std::string::npos) return false;
    const size_t b64Start = line.find_first_not_of(" \t", idEnd);
    if (b64Start == std::string::npos) return false;
    const std::string idHex = line.substr(idStart, idEnd - idStart);
    std::string b64 = line.substr(b64Start);
    while (!b64.empty() && (b64.back() == '\r' || b64.back() == ' ' || b64.back() == '\t')) b64.pop_back();
    if (idHex.empty() || idHex.size() > 16 || b64.empty()) return false;

    for (char c : idHex) {
        if (HexDigit(c) < 0) return false;
    }
    std::vector<BYTE> decoded;
    if (!Base64Decode(b64, decoded)) return false;
    if (!IsValidTemplate(decoded.data(), static_cast<UINT>(decoded.size()))) return false;

    out.payload = std::move(decoded);
    return true;
}

void TakeLines(std::string& buffer, std::vector<std::string>& lines) {
    size_t pos = 0;
    for (;;) {
        const size_t nl = buffer.find('\n', pos);
        if (nl == std::string::npos) break;
        lines.push_back(buffer.substr(pos, nl - pos));
        pos = nl + 1;
    }
    if (pos > 0) buffer.erase(0, pos);
}

bool TryPipe(Acquisition& out) {
    HANDLE pipe = CreateFileW(L"\\\\.\\pipe\\wfp-zk-shim",
                              GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                              OPEN_EXISTING, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return false;
    bool acquired = false;
    do {
        const char request[] = "ACQUIRE\n";
        DWORD written = 0;
        if (!WriteFile(pipe, request, sizeof(request) - 1, &written, nullptr)) break;

        std::string buffer;
        const ULONGLONG deadline = GetTickCount64() + 2000;
        while (GetTickCount64() < deadline) {
            DWORD available = 0;
            if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) break;
            if (available == 0) {
                Sleep(20);
                continue;
            }
            char chunk[2048];
            const DWORD want = available > sizeof(chunk) ? static_cast<DWORD>(sizeof(chunk)) : available;
            DWORD got = 0;
            if (!ReadFile(pipe, chunk, want, &got, nullptr) || got == 0) break;
            buffer.append(chunk, got);
            std::vector<std::string> lines;
            TakeLines(buffer, lines);
            for (const std::string& line : lines) {
                if (line == "OK") continue;
                if (ParseAcquireLine(line, out)) {
                    acquired = true;
                    break;
                }
            }
            if (acquired) break;
        }
        if (acquired) {
            const char ack[] = "OK\n";
            DWORD ackWritten = 0;
            WriteFile(pipe, ack, sizeof(ack) - 1, &ackWritten, nullptr);
        }
    } while (false);
    CloseHandle(pipe);
    return acquired;
}

HMODULE ThisModule() {
    HMODULE module = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&ThisModule), &module);
    return module;
}

bool TryConfigFile(Acquisition& out) {
    HMODULE module = ThisModule();
    if (!module) return false;
    wchar_t path[MAX_PATH];
    const DWORD length = GetModuleFileNameW(module, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return false;
    wchar_t* slash = std::wcsrchr(path, L'\\');
    if (!slash) return false;
    const wchar_t name[] = L"zkshim-config.txt";
    const size_t start = static_cast<size_t>(slash + 1 - path);
    if (MAX_PATH - start < sizeof(name) / sizeof(name[0])) return false;
    std::memcpy(slash + 1, name, sizeof(name));

    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    char bytes[8192];
    DWORD got = 0;
    const BOOL read = ReadFile(file, bytes, sizeof(bytes) - 1, &got, nullptr);
    CloseHandle(file);
    if (!read || got == 0) return false;
    bytes[got] = '\0';
    std::string line(bytes);
    const size_t nl = line.find('\n');
    if (nl != std::string::npos) line.resize(nl);
    while (!line.empty() && (line.back() == '\r' || line.back() == ' ' || line.back() == '\t')) line.pop_back();
    return ParseAcquireLine(line, out);
}

}  // namespace

extern "C" int __stdcall ZKFPM_Init(void) {
    return kOk;
}

extern "C" int __stdcall ZKFPM_Terminate(void) {
    return kOk;
}

extern "C" int __stdcall ZKFPM_GetDeviceCount(void) {
    return 1;
}

extern "C" HANDLE __stdcall ZKFPM_OpenDevice(int index) {
    if (index != 0) return nullptr;
    return reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(kDeviceHandle));
}

extern "C" int __stdcall ZKFPM_CloseDevice(HANDLE handle) {
    return handle ? kOk : kErrHandle;
}

extern "C" int __stdcall ZKFPM_SetParameters(HANDLE handle, int code, BYTE* value, UINT cbValue) {
    (void)handle;
    (void)code;
    (void)value;
    (void)cbValue;
    return kOk;
}

extern "C" int __stdcall ZKFPM_GetParameters(HANDLE handle, int code, BYTE* value, UINT* pcbValue) {
    (void)handle;
    (void)code;
    if (!value || !pcbValue) return kErrParam;
    const UINT cb = *pcbValue < sizeof(UINT) ? *pcbValue : sizeof(UINT);
    std::memset(value, 0, cb);
    *pcbValue = cb;
    return kOk;
}

extern "C" int __stdcall ZKFPM_GetCaptureParamsEx(HANDLE handle, int* width, int* height, int* dpi) {
    (void)handle;
    if (!width || !height || !dpi) return kErrParam;
    *width = 256;
    *height = 256;
    *dpi = 500;
    return kOk;
}

extern "C" int __stdcall ZKFPM_AcquireFingerprint(HANDLE handle, BYTE* image, UINT cbImage,
                                   BYTE* templ, UINT* pcbTemplate) {
    (void)handle;
    if (!templ || !pcbTemplate) return kErrParam;

    Acquisition acquisition;
    {
        std::lock_guard<std::mutex> lock(g_pipeMutex);
        if (!TryPipe(acquisition) && !TryConfigFile(acquisition)) return kErrCapture;
    }
    const UINT cb = static_cast<UINT>(acquisition.payload.size());
    if (cb == 0 || cb > *pcbTemplate) return kErrMemory;
    std::memcpy(templ, acquisition.payload.data(), cb);
    *pcbTemplate = cb;

    if (image && cbImage >= kImageSize) {
        for (UINT i = 0; i < kImageSize; ++i) image[i] = static_cast<BYTE>(i & 0xFFu);
    }
    return kOk;
}

extern "C" HANDLE __stdcall ZKFPM_DBInit(void) {
    std::lock_guard<std::mutex> lock(g_dbMutex);
    const ULONG_PTR handle = g_nextDbHandle++;
    g_dbs[handle];
    return reinterpret_cast<HANDLE>(handle);
}

extern "C" int __stdcall ZKFPM_DBFree(HANDLE db) {
    std::lock_guard<std::mutex> lock(g_dbMutex);
    g_dbs.erase(reinterpret_cast<ULONG_PTR>(db));
    return kOk;
}

extern "C" int __stdcall ZKFPM_DBAdd(HANDLE db, UINT fid, BYTE* templ, UINT cbTemplate) {
    if (!templ || !IsValidTemplate(templ, cbTemplate)) return kErrParam;
    std::lock_guard<std::mutex> lock(g_dbMutex);
    auto it = g_dbs.find(reinterpret_cast<ULONG_PTR>(db));
    if (it == g_dbs.end()) return kErrHandle;
    it->second[fid] = std::vector<BYTE>(templ, templ + cbTemplate);
    return kOk;
}

extern "C" int __stdcall ZKFPM_DBDel(HANDLE db, UINT fid) {
    std::lock_guard<std::mutex> lock(g_dbMutex);
    auto it = g_dbs.find(reinterpret_cast<ULONG_PTR>(db));
    if (it != g_dbs.end()) it->second.erase(fid);
    return kOk;
}

extern "C" int __stdcall ZKFPM_DBIdentify(HANDLE db, BYTE* templ, UINT cbTemplate,
                           UINT* fid, UINT* score) {
    if (!templ || !fid || !score) return kErrParam;
    if (!IsValidTemplate(templ, cbTemplate)) return kErrParam;
    *fid = 0;
    *score = 0;
    std::lock_guard<std::mutex> lock(g_dbMutex);
    auto it = g_dbs.find(reinterpret_cast<ULONG_PTR>(db));
    if (it == g_dbs.end()) return kErrHandle;
    for (const auto& entry : it->second) {
        if (TemplatesMatch(templ, cbTemplate, entry.second.data(),
                           static_cast<UINT>(entry.second.size()))) {
            *fid = entry.first;
            *score = 99;
            return kOk;
        }
    }
    return kErrNoMatch;
}

extern "C" int __stdcall ZKFPM_DBMatch(HANDLE db, BYTE* first, UINT cbFirst,
                        BYTE* second, UINT cbSecond) {
    (void)db;
    if (!first || !second || !IsValidTemplate(first, cbFirst) ||
        !IsValidTemplate(second, cbSecond)) return kErrParam;
    return TemplatesMatch(first, cbFirst, second, cbSecond) ? kOk : kErrNoMatch;
}

extern "C" int __stdcall ZKFPM_DBMerge(HANDLE db, BYTE* first, BYTE* second, BYTE* third,
                        BYTE* merged, UINT* pcbMerged) {
    (void)db;
    if (!first || !second || !third || !merged || !pcbMerged) return kErrParam;

    // No lengths are supplied: read each frame's declared length from its header.
    UINT sizes[3] = {0, 0, 0};
    const BYTE* inputs[3] = {first, second, third};
    for (int i = 0; i < 3; ++i) {
        if (!ParseFrame(inputs[i], kMaxTemplate, sizes[i])) return kErrParam;
    }
    const UINT total = sizes[0] + sizes[1] + sizes[2];
    if (total == 0 || total > kMaxTemplate) return kErrMerge;
    if (total > *pcbMerged) return kErrMemory;

    UINT offset = 0;
    for (int i = 0; i < 3; ++i) {
        std::memcpy(merged + offset, inputs[i], sizes[i]);
        offset += sizes[i];
    }
    *pcbMerged = total;
    return kOk;
}
