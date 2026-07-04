#include <windows.h>
#include <cstdint>
#include <vector>
#include <psapi.h>
#include <MinHook.h>
#include <cstdio>
#include <algorithm>
#include <cctype>
#include <cstdarg>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <intrin.h>
#include <iterator>
#include <string>
#include <utility>

// ========================================================================
// 1. DINPUT8 PROXY FORWARDER
// ========================================================================
typedef HRESULT(WINAPI *DirectInput8Create_t)(HINSTANCE, DWORD, REFIID, LPVOID*, LPUNKNOWN);
DirectInput8Create_t pOriginalDirectInput8Create = nullptr;

extern "C" __declspec(dllexport) HRESULT WINAPI DirectInput8Create(HINSTANCE hinst, DWORD dwVersion, REFIID riidltf, LPVOID* ppvOut, LPUNKNOWN punkOuter) {
    // If we haven't loaded the real dinput8.dll yet, do it now
    if (!pOriginalDirectInput8Create) {
        char syspath[MAX_PATH] = {};
        if (GetSystemDirectoryA(syspath, MAX_PATH) == 0) {
            return E_FAIL;
        }
        strcat_s(syspath, "\\dinput8.dll"); // Points to C:\Windows\System32\dinput8.dll
        HMODULE hMod = LoadLibraryA(syspath);
        if (hMod) {
            pOriginalDirectInput8Create = (DirectInput8Create_t)GetProcAddress(hMod, "DirectInput8Create");
        }
    }
    // Forward the call to the real Windows DirectX file
    if (pOriginalDirectInput8Create) {
        return pOriginalDirectInput8Create(hinst, dwVersion, riidltf, ppvOut, punkOuter);
    }
    return E_FAIL;
}

// ========================================================================
// 2. BLUESTACKS FIX (CUSTOM MATCHER)
// ========================================================================
struct IndexStruct {
    void* pad[2];
    uint32_t type;
};

struct ImgdState {
    IndexStruct* idxStruct; // +0x00
    uint8_t* colorBuf;      // +0x08
    uint8_t pad1[12];
    int stride;             // +0x1C
    int compCount;          // +0x20
    int type;               // +0x24
    uint8_t pad2[2];
    uint8_t flag;           // +0x2A
    uint8_t pad3[1];
    uint32_t cursor;        // +0x2C
    int maxVerts;           // +0x30
};

typedef uint8_t(*MatcherFn)(ImgdState*, int, short*, int, uint32_t);
MatcherFn pOriginalMatcher = nullptr;

void LogImageMarkerProbeCall(
    void* callerReturnAddress,
    ImgdState* state,
    int mode,
    int p4,
    uint32_t mc,
    void* idxData,
    uint32_t idxType,
    int stride);

void LogImageMarkerProbeScan(
    void* callerReturnAddress,
    ImgdState* state,
    int mode,
    int p4,
    uint32_t mc,
    void* idxData,
    uint32_t idxType,
    int stride);

void LogImageMarkerProbeMatch(
    void* callerReturnAddress,
    ImgdState* state,
    int mode,
    int p4,
    uint32_t mc,
    void* idxData,
    uint32_t idxType,
    int stride,
    uint32_t streamIndex,
    uint32_t v0,
    uint32_t v1,
    uint32_t v2,
    const uint8_t* colorPtr);

typedef void(*MOBASkillComputeAimCoordsFn)(void*, int, int, double*, double*, char);
MOBASkillComputeAimCoordsFn pOriginalMOBASkillComputeAimCoords = nullptr;

typedef HCURSOR(WINAPI *SetCursorFn)(HCURSOR);
SetCursorFn pOriginalSetCursor = nullptr;

typedef void(__fastcall *BlueStacksApplyCursorFn)(void*);
BlueStacksApplyCursorFn pOriginalBlueStacksApplyCursor = nullptr;

void ReportHookResolutionError(const char* targetName, const char* detail);

// Replacement for HD-Player's Imgd color marker matcher.
// This intentionally keeps the working runtime behavior from the original
// patch, even where it differs from the cleaner Ghidra decompile.
uint8_t CustomMatcherImpl(ImgdState* state, int mode, short* outIdx, int p4, uint32_t mc, void* callerReturnAddress) {
    // Low-risk safety guards. Do not add the Ghidra-only flag/mode guards here
    // without testing; those changed behavior in a way that broke detection.
    if (!state || !outIdx) return 0;
    if (state->compCount != 4 || state->type != 0x1401) return 0;

    // The target path is RGBA as four GL_UNSIGNED_BYTE components. A zero
    // stride means tightly packed colors, so each color entry is 4 bytes.
    int stride = state->stride == 0 ? 4 : state->stride;
    uint32_t cursor = state->cursor;
    if (state->maxVerts <= 0) return 0;

    uint32_t maxVerts = static_cast<uint32_t>(state->maxVerts);
    if (cursor >= maxVerts) return 0;

    uint8_t* colorBuf = state->colorBuf;
    if (!colorBuf) return 0;

    // idxStruct points at the draw index stream when the draw is indexed.
    // The currently working runtime layout reads the index type through this
    // struct shape. Test any offset change separately before keeping it.
    void* idxData = nullptr;
    uint32_t idxType = 0;
    if (state->idxStruct) {
        idxData = *(void**)state->idxStruct;
        idxType = state->idxStruct->type;
    }

    LogImageMarkerProbeCall(
        callerReturnAddress,
        state,
        mode,
        p4,
        mc,
        idxData,
        idxType,
        stride);
    LogImageMarkerProbeScan(
        callerReturnAddress,
        state,
        mode,
        p4,
        mc,
        idxData,
        idxType,
        stride);

    // mc is packed as 0xRRGGBBAA and compared byte-for-byte against the first
    // resolved vertex color in each candidate triangle.
    uint8_t t0 = (mc >> 24) & 0xFF, t1 = (mc >> 16) & 0xFF;
    uint8_t t2 = (mc >> 8) & 0xFF,  t3 = mc & 0xFF;

    // mode 5 needs overlapping candidates, so it advances one index at a time.
    // Other modes scan independent triangle triplets.
    int step = (mode == 5) ? 1 : 3;
    uint32_t endIdx = cursor;

    while (endIdx + 2 < maxVerts) {
        uint32_t v0, v1, v2;
        if (!idxData) {
            v0 = endIdx; v1 = endIdx + 1; v2 = endIdx + 2;
        } else if (idxType == 0x1401) {
            v0 = ((uint8_t*)idxData)[endIdx]; v1 = ((uint8_t*)idxData)[endIdx+1]; v2 = ((uint8_t*)idxData)[endIdx+2];
        } else if (idxType == 0x1403) {
            v0 = ((uint16_t*)idxData)[endIdx]; v1 = ((uint16_t*)idxData)[endIdx+1]; v2 = ((uint16_t*)idxData)[endIdx+2];
        } else if (idxType == 0x1405) {
            v0 = ((uint32_t*)idxData)[endIdx]; v1 = ((uint32_t*)idxData)[endIdx+1]; v2 = ((uint32_t*)idxData)[endIdx+2];
        } else {
            v0 = endIdx; v1 = endIdx + 1; v2 = endIdx + 2;
        }

        // This filter is part of the working patch behavior. The original
        // Ghidra function did not show this check, but removing it broke the
        // practical fix by allowing degenerate triangles to match.
        if (v0 == v1 || v1 == v2 || v0 == v2) {
            endIdx += step;
            continue;
        }

        // Only v0's color is the marker key. outIdx below still returns the
        // stream positions, not the resolved vertex ids, because the caller
        // expects texture-coordinate/index positions.
        uint8_t* cPtr = colorBuf + (v0 * stride);
        if (cPtr[0] == t0 && cPtr[1] == t1 && cPtr[2] == t2 && cPtr[3] == t3) {
            LogImageMarkerProbeMatch(
                callerReturnAddress,
                state,
                mode,
                p4,
                mc,
                idxData,
                idxType,
                stride,
                endIdx,
                v0,
                v1,
                v2,
                cPtr);
            outIdx[0] = endIdx; outIdx[1] = endIdx + 1; outIdx[2] = endIdx + 2;
            // Advance past the matched triplet so repeated calls do not return
            // the same marker forever.
            state->cursor = endIdx + 3;
            return 1;
        }
        endIdx += step;
    }
    return 0;
}

uint8_t CustomMatcher(ImgdState* state, int mode, short* outIdx, int p4, uint32_t mc) {
    __try {
        return CustomMatcherImpl(state, mode, outIdx, p4, mc, _ReturnAddress());
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ReportHookResolutionError("Imgd_FindColorMarkerTriangle", "The hook hit an exception while reading the updated runtime layout.");
        return 0;
    }
}

// ========================================================================
// 3. BRAWL STARS MOBASKILL AIM COMPENSATION
// ========================================================================
constexpr uintptr_t kMOBASkillComputeAimCoordsRva = 0x3C5690;
constexpr uintptr_t kBlueStacksApplyCursorRva = 0xF71E0;
constexpr double kMOBASkillScreenPercentMax = 100.0;
double gMOBASkillEdgeThresholdPercent = 25.0;
double gMOBASkillMaxAimXBiasPercent = 1.5;
DWORD gMOBASkillLastDebugPrintTick = 0;
double gMOBASkillLeftEdgeXPercent = 9.8;
double gMOBASkillRightEdgeXPercent = 90.2;
bool gDebugConsoleEnabled = false;
bool gDebugConsoleAttached = false;
bool gHookResolutionErrorShown = false;
bool gCustomCursorEnabled = false;
bool gImageMarkerProbeEnabled = false;
uint32_t gImageMarkerProbeColor = 0x48E03400;
std::string gCustomCursorMousePath;
std::string gCustomCursorMobaPath;
std::string gCustomCursorMobaRightPath;
std::string gCustomCursorBlankPath;
std::string gImageMarkerProbeLogPath;
HCURSOR gCustomCursorMouseHandle = nullptr;
HCURSOR gCustomCursorMobaHandle = nullptr;
HCURSOR gCustomCursorMobaRightHandle = nullptr;
HCURSOR gCustomCursorBlankHandle = nullptr;
CRITICAL_SECTION gCustomCursorLock = {};
bool gCustomCursorLockInitialized = false;
LONG gActiveBlueStacksCursorRole = 0;
LONG gImageMarkerProbeCallLogCount = 0;
LONG gImageMarkerProbeMatchLogCount = 0;
DWORD gImageMarkerProbeLastCallLogTick = 0;

enum BlueStacksCursorRole {
    kBlueStacksCursorRoleMouse = 0,
    kBlueStacksCursorRoleMoba = 1,
    kBlueStacksCursorRoleMobaRight = 2,
    kBlueStacksCursorRoleBlank = 3,
};

HMODULE gWrapperModuleHandle = nullptr;

double ClampDouble(double value, double minValue, double maxValue) {
    if (value < minValue) return minValue;
    if (value > maxValue) return maxValue;
    return value;
}

void DebugPrint(const char* format, ...) {
    char buf[1024] = {};
    va_list args;
    va_start(args, format);
    vsprintf_s(buf, format, args);
    va_end(args);

    if (gDebugConsoleAttached) {
        printf("%s", buf);
    }
    OutputDebugStringA(buf);
}

void SetupDebugConsole() {
    if (gDebugConsoleAttached) return;
    if (!AllocConsole()) return;

    FILE* stream = nullptr;
    freopen_s(&stream, "CONOUT$", "w", stdout);
    freopen_s(&stream, "CONOUT$", "w", stderr);
    SetConsoleTitleA("BlueStacks dinput8 hook debug");
    gDebugConsoleAttached = true;
    DebugPrint("BlueStacks dinput8 hook debug console attached\n");
}

void UpdateDebugConsoleVisibility() {
    if (gDebugConsoleEnabled) {
        SetupDebugConsole();
    } else if (gDebugConsoleAttached) {
        DebugPrint("BlueStacks dinput8 hook debug console detached\n");
        FreeConsole();
        gDebugConsoleAttached = false;
    }
}

std::wstring StringToWide(const std::string& text) {
    if (text.empty()) return {};

    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.c_str(), -1, nullptr, 0);
    UINT codePage = CP_UTF8;
    DWORD flags = MB_ERR_INVALID_CHARS;
    if (length == 0) {
        codePage = CP_ACP;
        flags = 0;
        length = MultiByteToWideChar(codePage, flags, text.c_str(), -1, nullptr, 0);
    }
    if (length == 0) return {};

    std::wstring wide(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(codePage, flags, text.c_str(), -1, wide.data(), length);
    wide.resize(static_cast<size_t>(length - 1));
    return wide;
}

bool HasCursorFileExtension(const std::string& path) {
    size_t dotPos = path.find_last_of('.');
    if (dotPos == std::string::npos) return false;

    std::string ext = path.substr(dotPos);
    std::transform(ext.begin(), ext.end(), ext.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return ext == ".cur" || ext == ".ani";
}

void DestroyCursorHandle(HCURSOR* handle) {
    if (*handle) {
        DestroyCursor(*handle);
        *handle = nullptr;
    }
}

void DestroyCustomCursorsLocked() {
    DestroyCursorHandle(&gCustomCursorMouseHandle);
    DestroyCursorHandle(&gCustomCursorMobaHandle);
    DestroyCursorHandle(&gCustomCursorMobaRightHandle);
    DestroyCursorHandle(&gCustomCursorBlankHandle);
}

HCURSOR LoadCustomCursorFile(const std::string& path, const char* roleName) {
    if (path.empty()) return nullptr;

    if (!HasCursorFileExtension(path)) {
        DebugPrint(
            "Custom cursor %s: unsupported file extension for %s; use .cur or .ani\n",
            roleName,
            path.c_str());
        return nullptr;
    }

    std::wstring widePath = StringToWide(path);
    HCURSOR handle = nullptr;
    if (!widePath.empty()) {
        handle = static_cast<HCURSOR>(LoadImageW(
            nullptr,
            widePath.c_str(),
            IMAGE_CURSOR,
            0,
            0,
            LR_LOADFROMFILE | LR_DEFAULTSIZE));
    }

    if (handle) {
        DebugPrint("Custom cursor %s loaded: %s\n", roleName, path.c_str());
    } else {
        DebugPrint(
            "Custom cursor %s: failed to load %s, GetLastError=%lu\n",
            roleName,
            path.c_str(),
            static_cast<unsigned long>(GetLastError()));
    }
    return handle;
}

void ReloadCustomCursors() {
    if (!gCustomCursorLockInitialized) return;

    EnterCriticalSection(&gCustomCursorLock);
    DestroyCustomCursorsLocked();

    if (gCustomCursorEnabled) {
        gCustomCursorMouseHandle = LoadCustomCursorFile(gCustomCursorMousePath, "mouse");
        gCustomCursorMobaHandle = LoadCustomCursorFile(gCustomCursorMobaPath, "moba");
        gCustomCursorMobaRightHandle = LoadCustomCursorFile(gCustomCursorMobaRightPath, "moba_right");
        gCustomCursorBlankHandle = LoadCustomCursorFile(gCustomCursorBlankPath, "blank");
    }

    LeaveCriticalSection(&gCustomCursorLock);
}

HCURSOR GetCustomCursorForRoleLocked(LONG role) {
    HCURSOR roleHandle = nullptr;
    switch (role) {
        case kBlueStacksCursorRoleMoba:
            roleHandle = gCustomCursorMobaHandle;
            break;
        case kBlueStacksCursorRoleMobaRight:
            roleHandle = gCustomCursorMobaRightHandle;
            break;
        case kBlueStacksCursorRoleBlank:
            roleHandle = gCustomCursorBlankHandle;
            break;
        case kBlueStacksCursorRoleMouse:
        default:
            roleHandle = gCustomCursorMouseHandle;
            break;
    }

    return roleHandle;
}

std::string ReadBlueStacksStdString(void* stringObj) {
    if (!stringObj) return {};

    auto* bytes = reinterpret_cast<uint8_t*>(stringObj);
    size_t length = *reinterpret_cast<size_t*>(bytes + 0x10);
    size_t capacity = *reinterpret_cast<size_t*>(bytes + 0x18);
    const char* data = capacity >= 0x10
        ? *reinterpret_cast<const char**>(bytes)
        : reinterpret_cast<const char*>(bytes);

    if (!data || length > 4096) return {};
    return std::string(data, length);
}

LONG IdentifyBlueStacksCursorRole(const std::string& style) {
    if (style == "moba") return kBlueStacksCursorRoleMoba;
    if (style == "moba_right") return kBlueStacksCursorRoleMobaRight;
    if (style == "blank") return kBlueStacksCursorRoleBlank;
    return kBlueStacksCursorRoleMouse;
}

void __fastcall CustomBlueStacksApplyCursor(void* styleStringObj) {
    std::string style = ReadBlueStacksStdString(styleStringObj);
    LONG role = IdentifyBlueStacksCursorRole(style);
    InterlockedExchange(&gActiveBlueStacksCursorRole, role);

    if (pOriginalBlueStacksApplyCursor) {
        pOriginalBlueStacksApplyCursor(styleStringObj);
    }
}

HCURSOR WINAPI CustomSetCursor(HCURSOR cursor) {
    if (!pOriginalSetCursor) {
        return cursor;
    }

    HCURSOR cursorToSet = cursor;
    if (cursor && gCustomCursorLockInitialized) {
        EnterCriticalSection(&gCustomCursorLock);
        HCURSOR customCursor = gCustomCursorEnabled
            ? GetCustomCursorForRoleLocked(InterlockedCompareExchange(&gActiveBlueStacksCursorRole, 0, 0))
            : nullptr;
        if (customCursor) {
            cursorToSet = customCursor;
        }
        HCURSOR previous = pOriginalSetCursor(cursorToSet);
        LeaveCriticalSection(&gCustomCursorLock);
        return previous;
    }

    return pOriginalSetCursor(cursorToSet);
}

void ReportHookResolutionError(const char* targetName, const char* detail) {
    char buf[512] = {};
    sprintf_s(
        buf,
        "BlueStacks dinput8 hook: %s. %s\n"
        "The affected wrapper feature will be disabled to avoid crashing BlueStacks.",
        targetName,
        detail ? detail : "");
    DebugPrint("%s\n", buf);

    if (!gHookResolutionErrorShown) {
        gHookResolutionErrorShown = true;
        MessageBoxA(
            nullptr,
            buf,
            "BlueStacks wrapper offset mismatch",
            MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
    }
}

// Hook for HD-Player's ImapMOBASkillComputeAimCoords. The original function
// computes the virtual skill-stick endpoint from mouse coordinates. After the
// original math runs, this nudges only the X endpoint when the character is
// close to a horizontal screen edge. Near the left edge, compensate left;
// near the right edge, compensate right.
void CustomMOBASkillComputeAimCoordsImpl(
    void* skillRuntime,
    int mouseX,
    int mouseY,
    double* outAimX,
    double* outAimY,
    char clampToDeadzone) {

    if (pOriginalMOBASkillComputeAimCoords) {
        pOriginalMOBASkillComputeAimCoords(skillRuntime, mouseX, mouseY, outAimX, outAimY, clampToDeadzone);
    }

    if (!skillRuntime || !outAimX) return;

    double originalAimX = *outAimX;
    double originalAimY = outAimY ? *outAimY : 0.0;
    double originXPercent = *reinterpret_cast<double*>(
        reinterpret_cast<uint8_t*>(skillRuntime) + 0xC8);
    double originYPercent = *reinterpret_cast<double*>(
        reinterpret_cast<uint8_t*>(skillRuntime) + 0xD0);
    double aimDeltaXPercent = originalAimX - originXPercent;
    double aimDeltaYPercent = originalAimY - originYPercent;

    // Ghidra: skillRuntime +0xD8 is the current character/control X position
    // in screen percent. The compute function already uses it as the mouse
    // delta center, so this is the safest edge-distance signal available here.
    double characterXPercent = *reinterpret_cast<double*>(
        reinterpret_cast<uint8_t*>(skillRuntime) + 0xD8);
    double characterYPercent = *reinterpret_cast<double*>(
        reinterpret_cast<uint8_t*>(skillRuntime) + 0xE0);

    if (characterXPercent < 0.0 || characterXPercent > kMOBASkillScreenPercentMax) return;

    double xBias = 0.0;
    double closeness = 0.0;
    double leftEdgeEnd = gMOBASkillLeftEdgeXPercent + gMOBASkillEdgeThresholdPercent;
    double rightEdgeStart = gMOBASkillRightEdgeXPercent - gMOBASkillEdgeThresholdPercent;

    if (characterXPercent < leftEdgeEnd) {
        closeness = (leftEdgeEnd - characterXPercent) /
            gMOBASkillEdgeThresholdPercent;
        closeness = ClampDouble(closeness, 0.0, 1.0);
        xBias = -(closeness * gMOBASkillMaxAimXBiasPercent);
    } else if (characterXPercent > rightEdgeStart) {
        closeness = (characterXPercent - rightEdgeStart) /
            gMOBASkillEdgeThresholdPercent;
        closeness = ClampDouble(closeness, 0.0, 1.0);
        xBias = closeness * gMOBASkillMaxAimXBiasPercent;
    }

    if (xBias != 0.0) {
        bool reversedForDownwardAim = aimDeltaYPercent > 0.0;
        if (reversedForDownwardAim) {
            xBias = -xBias;
        }

        // Do not clamp the aim endpoint to 0..100. The original function can
        // intentionally return values outside screen percent bounds for long
        // aim vectors, and clamping here changes the shot direction.
        *outAimX = *outAimX + xBias;
    }

    DWORD now = GetTickCount();
    if (now - gMOBASkillLastDebugPrintTick >= 100) {
        gMOBASkillLastDebugPrintTick = now;
        bool reversedForDownwardAim = xBias != 0.0 && aimDeltaYPercent > 0.0;
        DebugPrint(
            "MOBASkill pos=(%.2f, %.2f) origin=(%.2f, %.2f) aimDelta=(%.2f, %.2f) edgeAnchors=(%.2f, %.2f) edgeZones=(%.2f..%.2f, %.2f..%.2f) mouse=(%d,%d) aim=(%.2f, %.2f)->(%.2f, %.2f) closeness=%.2f reversedDown=%d xBias=%.2f strength=%.2f edgeWidth=%.2f deadzone=%d\n",
            characterXPercent,
            characterYPercent,
            originXPercent,
            originYPercent,
            aimDeltaXPercent,
            aimDeltaYPercent,
            gMOBASkillLeftEdgeXPercent,
            gMOBASkillRightEdgeXPercent,
            gMOBASkillLeftEdgeXPercent,
            leftEdgeEnd,
            rightEdgeStart,
            gMOBASkillRightEdgeXPercent,
            mouseX,
            mouseY,
            originalAimX,
            originalAimY,
            *outAimX,
            outAimY ? *outAimY : 0.0,
            closeness,
            static_cast<int>(reversedForDownwardAim),
            xBias,
            gMOBASkillMaxAimXBiasPercent,
            gMOBASkillEdgeThresholdPercent,
            static_cast<int>(clampToDeadzone));
    }
}

void CustomMOBASkillComputeAimCoords(
    void* skillRuntime,
    int mouseX,
    int mouseY,
    double* outAimX,
    double* outAimY,
    char clampToDeadzone) {

    __try {
        CustomMOBASkillComputeAimCoordsImpl(skillRuntime, mouseX, mouseY, outAimX, outAimY, clampToDeadzone);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ReportHookResolutionError("ImapRtMOBASkill_computeAimCoords", "The hook hit an exception while handling the updated runtime layout.");
    }
}

uintptr_t FindPattern(HMODULE hMod, const std::vector<int>& pattern) {
    if (!hMod || pattern.empty()) return 0;

    MODULEINFO info = {0};
    if (!GetModuleInformation(GetCurrentProcess(), hMod, &info, sizeof(info))) return 0;

    uint8_t* base = (uint8_t*)info.lpBaseOfDll;
    DWORD size = info.SizeOfImage;
    if (!base || size < pattern.size()) return 0;

    DWORD lastStart = size - static_cast<DWORD>(pattern.size());
    for (DWORD i = 0; i <= lastStart; i++) {
        bool found = true;
        for (size_t j = 0; j < pattern.size(); j++) {
            if (pattern[j] != -1 && base[i + j] != pattern[j]) {
                found = false; break;
            }
        }
        if (found) return (uintptr_t)(base + i);
    }
    return 0;
}

bool IsRvaInsideModule(HMODULE hMod, uintptr_t rva);

uintptr_t ResolveFunction(HMODULE hMod, const std::vector<int>& pattern, uintptr_t fallbackRva) {
    uintptr_t found = FindPattern(hMod, pattern);
    if (found) return found;
    DebugPrint("Signature resolution failed; fallback RVA 0x%p inside module=%d\n",
        reinterpret_cast<void*>(fallbackRva),
        static_cast<int>(IsRvaInsideModule(hMod, fallbackRva)));
    return 0;
}

bool IsRvaInsideModule(HMODULE hMod, uintptr_t rva) {
    if (!hMod) return false;

    MODULEINFO info = {0};
    if (!GetModuleInformation(GetCurrentProcess(), hMod, &info, sizeof(info))) return false;

    return info.lpBaseOfDll && rva < info.SizeOfImage;
}

// ========================================================================
// 4. LIVE KMM CFG RELOAD
// ========================================================================
constexpr uintptr_t kKmmSetSchemeByNameRva = 0x4029D0;
constexpr uintptr_t kKmmLoadPackageCfgRva = 0x3FB360;
constexpr uintptr_t kKmmSetActiveCfgRva = 0x402610;
constexpr uintptr_t kKmmDestroyCfgRva = 0x3F3CC0;
constexpr uintptr_t kQtInvokeQVariantMethodRva = 0x32950;
constexpr uintptr_t kQStringDtorThunkRva = 0xCDFAAC;
constexpr uintptr_t kQStringFromStdStringThunkRva = 0xCDFAC4;
constexpr uintptr_t kQVariantDtorThunkRva = 0xCDFC08;
constexpr uintptr_t kQVariantFromQStringThunkRva = 0xCDFC1A;
constexpr uintptr_t kQVariantMetaTypeInterfaceRva = 0x1A0D2E0;
constexpr uintptr_t kKmmGlobalStatePtrRva = 0x1A719B0;
constexpr const char* kBrawlStarsPackageName = "com.supercell.brawlstars";
using KmmSetSchemeByNameFn = unsigned int (*)(const std::string*);
using KmmLoadPackageCfgFn = void* (*)(void*, const std::string*);
using KmmSetActiveCfgFn = __int64 (*)(void*, char);
using KmmDestroyCfgFn = void (*)(void*);
using QStringFromStdStringFn = void* (*)(void*, const std::string*);
using QStringDtorFn = void (*)(void*);
using QVariantFromQStringFn = void* (*)(void*, const void*);
using QVariantDtorFn = void (*)(void*);
using QtInvokeQVariantMethodFn = __int64 (*)(uintptr_t, const char*, void*);

struct QtQVariantInvokeArg {
    void* metaTypeInterface;
    const char* typeName;
    void* value;
};

bool GetFileWriteTime(const std::string& path, FILETIME* writeTime) {
    WIN32_FILE_ATTRIBUTE_DATA data = {};
    if (!GetFileAttributesExA(path.c_str(), GetFileExInfoStandard, &data)) return false;
    *writeTime = data.ftLastWriteTime;
    return true;
}

bool FileTimesDiffer(const FILETIME& left, const FILETIME& right) {
    return CompareFileTime(&left, &right) != 0;
}

std::string GetBlueStacksDataRoot() {
    char value[MAX_PATH] = {};
    DWORD valueSize = sizeof(value);
    HKEY key = nullptr;
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, "SOFTWARE\\BlueStacks_nxt", 0, KEY_READ, &key) == ERROR_SUCCESS) {
        DWORD type = 0;
        if (RegQueryValueExA(key, "UserDefinedDir", nullptr, &type, reinterpret_cast<LPBYTE>(value), &valueSize) == ERROR_SUCCESS &&
            (type == REG_SZ || type == REG_EXPAND_SZ) &&
            value[0] != '\0') {
            RegCloseKey(key);
            return value;
        }
        RegCloseKey(key);
    }
    return "C:\\ProgramData\\BlueStacks_nxt";
}

std::string GetBrawlStarsLiveCfgPath() {
    std::string root = GetBlueStacksDataRoot();
    while (!root.empty() && (root.back() == '\\' || root.back() == '/')) {
        root.pop_back();
    }
    return root + "\\Engine\\UserData\\InputMapper\\UserFiles\\" + kBrawlStarsPackageName + ".cfg";
}

std::string GetLiveUserFilesFolder() {
    std::string root = GetBlueStacksDataRoot();
    while (!root.empty() && (root.back() == '\\' || root.back() == '/')) {
        root.pop_back();
    }
    return root + "\\Engine\\UserData\\InputMapper\\UserFiles";
}

std::string GetWrapperCfgPath() {
    std::string root = GetBlueStacksDataRoot();
    while (!root.empty() && (root.back() == '\\' || root.back() == '/')) {
        root.pop_back();
    }
    return root + "\\Engine\\UserData\\dinput8-config.json";
}

std::string GetDefaultImageMarkerProbeLogPath() {
    std::string root = GetBlueStacksDataRoot();
    while (!root.empty() && (root.back() == '\\' || root.back() == '/')) {
        root.pop_back();
    }
    return root + "\\Engine\\UserData\\dinput8-image-marker-probe.log";
}

std::string GetImageMarkerProbeLogPath() {
    if (!gImageMarkerProbeLogPath.empty()) {
        return gImageMarkerProbeLogPath;
    }
    return GetDefaultImageMarkerProbeLogPath();
}

void AppendImageMarkerProbeLogLine(const char* line) {
    if (!line || line[0] == '\0') return;

    std::string path = GetImageMarkerProbeLogPath();
    HANDLE file = CreateFileA(
        path.c_str(),
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return;
    }

    DWORD written = 0;
    DWORD length = static_cast<DWORD>(std::strlen(line));
    WriteFile(file, line, length, &written, nullptr);
    CloseHandle(file);
}

void LogImageMarkerProbeCall(
    void* callerReturnAddress,
    ImgdState* state,
    int mode,
    int p4,
    uint32_t mc,
    void* idxData,
    uint32_t idxType,
    int stride) {

    if (!gImageMarkerProbeEnabled || mc != gImageMarkerProbeColor || !state) return;

    LONG count = InterlockedIncrement(&gImageMarkerProbeCallLogCount);
    DWORD now = GetTickCount();
    if (count > 50 && now - gImageMarkerProbeLastCallLogTick < 1000) {
        return;
    }
    gImageMarkerProbeLastCallLogTick = now;

    char line[768] = {};
    sprintf_s(
        line,
        "CALL tick=%lu count=%ld caller=%p state=%p mode=%d p4=%d mc=0x%08X cursor=%u maxVerts=%d stride=%d comp=%d type=0x%X flag=0x%02X idxStruct=%p idxData=%p idxType=0x%X colorBuf=%p\n",
        static_cast<unsigned long>(now),
        static_cast<long>(count),
        callerReturnAddress,
        state,
        mode,
        p4,
        mc,
        state->cursor,
        state->maxVerts,
        stride,
        state->compCount,
        state->type,
        state->flag,
        state->idxStruct,
        idxData,
        idxType,
        state->colorBuf);
    AppendImageMarkerProbeLogLine(line);
}

template<typename T>
int FindFirstIndexPosition(const T* data, uint32_t maxEntries, uint32_t vertex) {
    if (!data) return -1;
    for (uint32_t i = 0; i < maxEntries; ++i) {
        if (static_cast<uint32_t>(data[i]) == vertex) {
            return static_cast<int>(i);
        }
    }
    return -1;
}

void LogImageMarkerProbeScan(
    void* callerReturnAddress,
    ImgdState* state,
    int mode,
    int p4,
    uint32_t mc,
    void* idxData,
    uint32_t idxType,
    int stride) {

    if (!gImageMarkerProbeEnabled || mc != gImageMarkerProbeColor || !state || !state->colorBuf) return;

    // CALL logging is already throttled. Reuse that count so the deeper scan
    // happens only on the first few probe hits plus occasional later samples.
    LONG callCount = gImageMarkerProbeCallLogCount;
    if (callCount > 60 && (callCount % 50) != 0) {
        return;
    }

    uint8_t t0 = (mc >> 24) & 0xFF;
    uint8_t t1 = (mc >> 16) & 0xFF;
    uint8_t t2 = (mc >> 8) & 0xFF;
    uint8_t t3 = mc & 0xFF;

    uint32_t maxVerts = state->maxVerts > 0 ? static_cast<uint32_t>(state->maxVerts) : 0;
    uint32_t exactCount = 0;
    char details[512] = {};
    size_t used = 0;

    for (uint32_t vertex = 0; vertex < maxVerts; ++vertex) {
        const uint8_t* cPtr = state->colorBuf + (vertex * stride);
        if (cPtr[0] != t0 || cPtr[1] != t1 || cPtr[2] != t2 || cPtr[3] != t3) {
            continue;
        }

        int pos8 = -1;
        int pos16 = -1;
        int pos32 = -1;
        if (idxData) {
            pos8 = FindFirstIndexPosition(reinterpret_cast<const uint8_t*>(idxData), maxVerts, vertex);
            pos16 = FindFirstIndexPosition(reinterpret_cast<const uint16_t*>(idxData), maxVerts, vertex);
            pos32 = FindFirstIndexPosition(reinterpret_cast<const uint32_t*>(idxData), maxVerts, vertex);
        }

        if (exactCount < 8) {
            int written = sprintf_s(
                details + used,
                sizeof(details) - used,
                " v%u(idx8=%d idx16=%d idx32=%d)",
                vertex,
                pos8,
                pos16,
                pos32);
            if (written > 0) {
                used += static_cast<size_t>(written);
                if (used >= sizeof(details)) {
                    used = sizeof(details) - 1;
                }
            }
        }
        ++exactCount;
    }

    char line[1024] = {};
    sprintf_s(
        line,
        "SCAN tick=%lu caller=%p state=%p mode=%d p4=%d mc=0x%08X exactVertices=%u cursor=%u maxVerts=%u stride=%d idxType=0x%X idxData=%p%s\n",
        static_cast<unsigned long>(GetTickCount()),
        callerReturnAddress,
        state,
        mode,
        p4,
        mc,
        exactCount,
        state->cursor,
        maxVerts,
        stride,
        idxType,
        idxData,
        details);
    AppendImageMarkerProbeLogLine(line);
}

void LogImageMarkerProbeMatch(
    void* callerReturnAddress,
    ImgdState* state,
    int mode,
    int p4,
    uint32_t mc,
    void* idxData,
    uint32_t idxType,
    int stride,
    uint32_t streamIndex,
    uint32_t v0,
    uint32_t v1,
    uint32_t v2,
    const uint8_t* colorPtr) {

    if (!gImageMarkerProbeEnabled || mc != gImageMarkerProbeColor || !state || !colorPtr) return;

    LONG count = InterlockedIncrement(&gImageMarkerProbeMatchLogCount);
    if (count > 500) {
        return;
    }

    DWORD now = GetTickCount();
    char line[1024] = {};
    sprintf_s(
        line,
        "MATCH tick=%lu count=%ld caller=%p state=%p mode=%d p4=%d mc=0x%08X stream=(%u,%u,%u) vertices=(%u,%u,%u) color=(%02X,%02X,%02X,%02X) cursorBefore=%u maxVerts=%d stride=%d comp=%d type=0x%X flag=0x%02X idxStruct=%p idxData=%p idxType=0x%X colorBuf=%p\n",
        static_cast<unsigned long>(now),
        static_cast<long>(count),
        callerReturnAddress,
        state,
        mode,
        p4,
        mc,
        streamIndex,
        streamIndex + 1,
        streamIndex + 2,
        v0,
        v1,
        v2,
        colorPtr[0],
        colorPtr[1],
        colorPtr[2],
        colorPtr[3],
        state->cursor,
        state->maxVerts,
        stride,
        state->compCount,
        state->type,
        state->flag,
        state->idxStruct,
        idxData,
        idxType,
        state->colorBuf);
    AppendImageMarkerProbeLogLine(line);
}

std::string GetBrawlStarsReloadRequestPath() {
    return GetBrawlStarsLiveCfgPath() + ".reload";
}

struct ReloadMarkerState {
    std::string reloadPath;
    std::string cfgPath;
    std::string packageName;
    FILETIME writeTime = {};
};

struct DesiredCfgState {
    std::string cfgPath;
    std::string packageName;
    std::string contents;
};

CRITICAL_SECTION gDesiredCfgLock = {};
bool gDesiredCfgLockInitialized = false;
std::vector<DesiredCfgState> gDesiredCfgStates;

std::string GetPackageNameFromCfgPath(const std::string& cfgPath) {
    size_t slashPos = cfgPath.find_last_of("\\/");
    size_t nameStart = slashPos == std::string::npos ? 0 : slashPos + 1;
    size_t cfgExtPos = cfgPath.rfind(".cfg");
    if (cfgExtPos == std::string::npos || cfgExtPos < nameStart) {
        return {};
    }

    return cfgPath.substr(nameStart, cfgExtPos - nameStart);
}

std::vector<ReloadMarkerState> DiscoverReloadMarkers() {
    std::vector<ReloadMarkerState> markers;
    std::string userFilesFolder = GetLiveUserFilesFolder();
    std::string searchPath = userFilesFolder + "\\*.cfg.reload";

    WIN32_FIND_DATAA findData = {};
    HANDLE findHandle = FindFirstFileA(searchPath.c_str(), &findData);
    if (findHandle == INVALID_HANDLE_VALUE) {
        return markers;
    }

    do {
        if ((findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
            continue;
        }

        std::string reloadPath = userFilesFolder + "\\" + findData.cFileName;
        std::string cfgPath = reloadPath;
        constexpr const char* reloadSuffix = ".reload";
        if (cfgPath.size() <= std::strlen(reloadSuffix)) {
            continue;
        }

        cfgPath.resize(cfgPath.size() - std::strlen(reloadSuffix));
        std::string packageName = GetPackageNameFromCfgPath(cfgPath);
        if (packageName.empty()) {
            continue;
        }

        ReloadMarkerState marker = {};
        marker.reloadPath = std::move(reloadPath);
        marker.cfgPath = std::move(cfgPath);
        marker.packageName = std::move(packageName);
        marker.writeTime = findData.ftLastWriteTime;
        markers.push_back(std::move(marker));
    } while (FindNextFileA(findHandle, &findData));

    FindClose(findHandle);
    return markers;
}

const ReloadMarkerState* FindReloadMarkerState(
    const std::vector<ReloadMarkerState>& markers,
    const std::string& reloadPath) {

    for (const ReloadMarkerState& marker : markers) {
        if (marker.reloadPath == reloadPath) {
            return &marker;
        }
    }

    return nullptr;
}

bool ReadWholeFile(const std::string& path, std::string* contents) {
    std::ifstream input(path, std::ios::binary);
    if (!input) return false;
    contents->assign(std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>());
    return !contents->empty();
}

bool WriteWholeFileAtomically(const std::string& path, const std::string& contents) {
    size_t slashPos = path.find_last_of("\\/");
    std::string folder = slashPos == std::string::npos ? "." : path.substr(0, slashPos);
    std::string fileName = slashPos == std::string::npos ? path : path.substr(slashPos + 1);

    std::string tempPath = folder + "\\" + fileName +
        ".restore." +
        std::to_string(GetCurrentProcessId()) +
        "." +
        std::to_string(GetCurrentThreadId()) +
        "." +
        std::to_string(GetTickCount()) +
        ".tmp";

    HANDLE file = CreateFileA(
        tempPath.c_str(),
        GENERIC_WRITE,
        0,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        DebugPrint("KMM live reload: failed to create temp restore file %s error=%lu\n", tempPath.c_str(), GetLastError());
        return false;
    }

    bool ok = true;
    const char* data = contents.data();
    size_t remaining = contents.size();
    while (remaining > 0) {
        DWORD chunk = static_cast<DWORD>(std::min<size_t>(remaining, 1024 * 1024));
        DWORD written = 0;
        if (!WriteFile(file, data, chunk, &written, nullptr) || written != chunk) {
            DebugPrint("KMM live reload: failed to write temp restore file %s error=%lu\n", tempPath.c_str(), GetLastError());
            ok = false;
            break;
        }

        data += written;
        remaining -= written;
    }

    if (ok) {
        FlushFileBuffers(file);
    }

    CloseHandle(file);

    if (!ok) {
        DeleteFileA(tempPath.c_str());
        return false;
    }

    if (!MoveFileExA(tempPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_COPY_ALLOWED | MOVEFILE_WRITE_THROUGH)) {
        DebugPrint("KMM live reload: failed to replace cfg %s from %s error=%lu\n", path.c_str(), tempPath.c_str(), GetLastError());
        DeleteFileA(tempPath.c_str());
        return false;
    }

    return true;
}

void RememberEditorSavedCfg(const std::string& cfgPath, const std::string& packageName) {
    if (!gDesiredCfgLockInitialized) return;

    std::string contents;
    if (!ReadWholeFile(cfgPath, &contents)) {
        DebugPrint("KMM live reload: could not cache editor-saved cfg %s\n", cfgPath.c_str());
        return;
    }

    EnterCriticalSection(&gDesiredCfgLock);
    auto existing = std::find_if(
        gDesiredCfgStates.begin(),
        gDesiredCfgStates.end(),
        [&](const DesiredCfgState& state) {
            return state.cfgPath == cfgPath;
        });

    if (existing == gDesiredCfgStates.end()) {
        gDesiredCfgStates.push_back(DesiredCfgState{ cfgPath, packageName, std::move(contents) });
    } else {
        existing->packageName = packageName;
        existing->contents = std::move(contents);
    }

    LeaveCriticalSection(&gDesiredCfgLock);
    DebugPrint("KMM live reload: cached editor-saved cfg package=%s path=%s\n", packageName.c_str(), cfgPath.c_str());
}

void RestoreEditorSavedCfgsSnapshot(const std::vector<DesiredCfgState>& snapshot) {
    for (const DesiredCfgState& state : snapshot) {
        std::string currentContents;
        bool currentRead = ReadWholeFile(state.cfgPath, &currentContents);
        if (currentRead && currentContents == state.contents) {
            continue;
        }

        if (WriteWholeFileAtomically(state.cfgPath, state.contents)) {
            DebugPrint(
                "KMM live reload: restored editor-saved cfg package=%s path=%s\n",
                state.packageName.c_str(),
                state.cfgPath.c_str());
        }
    }
}

void RestoreEditorSavedCfgs() {
    if (!gDesiredCfgLockInitialized) return;

    EnterCriticalSection(&gDesiredCfgLock);
    std::vector<DesiredCfgState> snapshot = gDesiredCfgStates;
    LeaveCriticalSection(&gDesiredCfgLock);

    RestoreEditorSavedCfgsSnapshot(snapshot);
}

void RestoreEditorSavedCfgsOnProcessDetach() {
    if (!gDesiredCfgLockInitialized) return;

    RestoreEditorSavedCfgsSnapshot(gDesiredCfgStates);
}

uintptr_t ModuleAddressFromRva(HMODULE hMod, uintptr_t rva) {
    if (!IsRvaInsideModule(hMod, rva)) return 0;
    return reinterpret_cast<uintptr_t>(hMod) + rva;
}

bool IsJsonWhitespace(char c) {
    return c == ' ' || c == '\t' || c == '\r' || c == '\n';
}

size_t SkipJsonWhitespace(const std::string& json, size_t pos, size_t end) {
    while (pos < end && IsJsonWhitespace(json[pos])) {
        ++pos;
    }
    return pos;
}

int HexDigitValue(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
    if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
    return -1;
}

bool ParseJsonStringAt(const std::string& json, size_t quotePos, size_t end, std::string* value, size_t* nextPos) {
    if (quotePos >= end || json[quotePos] != '"') return false;

    std::string parsed;
    for (size_t pos = quotePos + 1; pos < end; ++pos) {
        char c = json[pos];
        if (c == '"') {
            *value = std::move(parsed);
            *nextPos = pos + 1;
            return true;
        }

        if (c != '\\') {
            parsed.push_back(c);
            continue;
        }

        if (++pos >= end) return false;
        char escaped = json[pos];
        switch (escaped) {
        case '"': parsed.push_back('"'); break;
        case '\\': parsed.push_back('\\'); break;
        case '/': parsed.push_back('/'); break;
        case 'b': parsed.push_back('\b'); break;
        case 'f': parsed.push_back('\f'); break;
        case 'n': parsed.push_back('\n'); break;
        case 'r': parsed.push_back('\r'); break;
        case 't': parsed.push_back('\t'); break;
        case 'u':
            if (pos + 4 >= end) return false;
            {
                int value16 = 0;
                for (int i = 0; i < 4; ++i) {
                    int digit = HexDigitValue(json[pos + 1 + i]);
                    if (digit < 0) return false;
                    value16 = (value16 << 4) | digit;
                }
                pos += 4;
                parsed.push_back(value16 >= 0 && value16 <= 0x7F ? static_cast<char>(value16) : '?');
            }
            break;
        default:
            parsed.push_back(escaped);
            break;
        }
    }

    return false;
}

size_t FindMatchingJsonToken(const std::string& json, size_t openPos, size_t end, char openToken, char closeToken) {
    if (openPos >= end || json[openPos] != openToken) return std::string::npos;

    std::vector<char> expectedClosers;
    expectedClosers.push_back(closeToken);
    for (size_t pos = openPos; pos < end; ++pos) {
        char c = json[pos];
        if (c == '"') {
            std::string ignored;
            size_t afterString = pos;
            if (!ParseJsonStringAt(json, pos, end, &ignored, &afterString)) {
                return std::string::npos;
            }
            pos = afterString - 1;
            continue;
        }

        if (pos != openPos && (c == '{' || c == '[')) {
            expectedClosers.push_back(c == '{' ? '}' : ']');
        } else if (c == '}' || c == ']') {
            if (expectedClosers.empty() || expectedClosers.back() != c) {
                return std::string::npos;
            }

            expectedClosers.pop_back();
            if (expectedClosers.empty()) {
                return pos;
            }
        }
    }

    return std::string::npos;
}

bool FindTopLevelJsonKeyValueStart(
    const std::string& json,
    size_t objectStart,
    size_t objectEnd,
    const char* key,
    size_t* valueStart) {

    if (objectStart >= objectEnd || json[objectStart] != '{') return false;

    int depth = 1;
    for (size_t pos = objectStart + 1; pos < objectEnd; ++pos) {
        char c = json[pos];
        if (c == '"') {
            std::string parsedKey;
            size_t afterString = pos;
            if (!ParseJsonStringAt(json, pos, objectEnd, &parsedKey, &afterString)) {
                return false;
            }

            if (depth == 1 && parsedKey == key) {
                size_t colonPos = SkipJsonWhitespace(json, afterString, objectEnd);
                if (colonPos < objectEnd && json[colonPos] == ':') {
                    *valueStart = SkipJsonWhitespace(json, colonPos + 1, objectEnd);
                    return true;
                }
            }

            pos = afterString - 1;
            continue;
        }

        if (c == '{' || c == '[') {
            ++depth;
        } else if (c == '}' || c == ']') {
            --depth;
            if (depth <= 0) break;
        }
    }

    return false;
}

bool ParseJsonBoolAt(const std::string& json, size_t pos, size_t end, bool* value) {
    if (pos + 4 <= end && json.compare(pos, 4, "true") == 0) {
        *value = true;
        return true;
    }
    if (pos + 5 <= end && json.compare(pos, 5, "false") == 0) {
        *value = false;
        return true;
    }
    return false;
}

bool ExtractSelectedSchemeNameFromCfg(const std::string& cfgPath, std::string* schemeName) {
    std::string json;
    if (!ReadWholeFile(cfgPath, &json)) {
        DebugPrint("KMM live reload: failed to read cfg for selected scheme: %s\n", cfgPath.c_str());
        return false;
    }

    size_t controlSchemesKey = json.find("\"ControlSchemes\"");
    if (controlSchemesKey == std::string::npos) {
        DebugPrint("KMM live reload: ControlSchemes key not found in %s\n", cfgPath.c_str());
        return false;
    }

    size_t arrayStart = json.find('[', controlSchemesKey);
    if (arrayStart == std::string::npos) {
        DebugPrint("KMM live reload: ControlSchemes array not found in %s\n", cfgPath.c_str());
        return false;
    }

    size_t arrayEnd = FindMatchingJsonToken(json, arrayStart, json.size(), '[', ']');
    if (arrayEnd == std::string::npos) {
        DebugPrint("KMM live reload: ControlSchemes array is malformed in %s\n", cfgPath.c_str());
        return false;
    }

    for (size_t pos = arrayStart + 1; pos < arrayEnd;) {
        pos = SkipJsonWhitespace(json, pos, arrayEnd);
        if (pos >= arrayEnd) break;
        if (json[pos] == ',') {
            ++pos;
            continue;
        }
        if (json[pos] != '{') {
            ++pos;
            continue;
        }

        size_t objectEnd = FindMatchingJsonToken(json, pos, arrayEnd + 1, '{', '}');
        if (objectEnd == std::string::npos) {
            DebugPrint("KMM live reload: scheme object is malformed in %s\n", cfgPath.c_str());
            return false;
        }

        size_t selectedValuePos = 0;
        bool selected = false;
        if (FindTopLevelJsonKeyValueStart(json, pos, objectEnd, "Selected", &selectedValuePos) &&
            ParseJsonBoolAt(json, selectedValuePos, objectEnd, &selected) &&
            selected) {

            size_t nameValuePos = 0;
            size_t afterName = 0;
            std::string parsedName;
            if (FindTopLevelJsonKeyValueStart(json, pos, objectEnd, "Name", &nameValuePos) &&
                ParseJsonStringAt(json, nameValuePos, objectEnd, &parsedName, &afterName) &&
                !parsedName.empty()) {

                *schemeName = std::move(parsedName);
                return true;
            }
        }

        pos = objectEnd + 1;
    }

    DebugPrint("KMM live reload: no selected scheme found in %s\n", cfgPath.c_str());
    return false;
}

bool ShowSchemeChangedToastUnsafe(HMODULE hdPlayerModule, const std::string& schemeName) {
    uintptr_t base = reinterpret_cast<uintptr_t>(hdPlayerModule);
    uintptr_t globalStatePtrAddress = ModuleAddressFromRva(hdPlayerModule, kKmmGlobalStatePtrRva);
    if (!globalStatePtrAddress) return false;

    uintptr_t kmmState = *reinterpret_cast<uintptr_t*>(globalStatePtrAddress);
    if (!kmmState) {
        DebugPrint("KMM live reload: qword_141A719B0 is null; toast skipped\n");
        return false;
    }

    uintptr_t qmlObject = *reinterpret_cast<uintptr_t*>(kmmState);
    if (!qmlObject) {
        DebugPrint("KMM live reload: KMM QML object is null; toast skipped\n");
        return false;
    }

    auto qStringFromStdString = reinterpret_cast<QStringFromStdStringFn>(base + kQStringFromStdStringThunkRva);
    auto qStringDtor = reinterpret_cast<QStringDtorFn>(base + kQStringDtorThunkRva);
    auto qVariantFromQString = reinterpret_cast<QVariantFromQStringFn>(base + kQVariantFromQStringThunkRva);
    auto qVariantDtor = reinterpret_cast<QVariantDtorFn>(base + kQVariantDtorThunkRva);
    auto invokeMethod = reinterpret_cast<QtInvokeQVariantMethodFn>(base + kQtInvokeQVariantMethodRva);

    alignas(16) unsigned char qStringStorage[64] = {};
    alignas(16) unsigned char qVariantStorage[64] = {};
    bool qStringConstructed = false;
    bool qVariantConstructed = false;

    qStringFromStdString(qStringStorage, &schemeName);
    qStringConstructed = true;
    qVariantFromQString(qVariantStorage, qStringStorage);
    qVariantConstructed = true;

    QtQVariantInvokeArg arg = {
        reinterpret_cast<void*>(base + kQVariantMetaTypeInterfaceRva),
        "QVariant",
        qVariantStorage
    };

    invokeMethod(qmlObject, "fShowSchemeChangedToast", &arg);

    if (qVariantConstructed) {
        qVariantDtor(qVariantStorage);
    }
    if (qStringConstructed) {
        qStringDtor(qStringStorage);
    }

    return true;
}

bool SetSelectedSchemeAndToastSafely(
    HMODULE hdPlayerModule,
    KmmSetSchemeByNameFn setSchemeByNameFn,
    const std::string& schemeName) {

    __try {
        setSchemeByNameFn(&schemeName);
        ShowSchemeChangedToastUnsafe(hdPlayerModule, schemeName);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ReportHookResolutionError("KMM direct scheme switch failed", "The internal SetScheme/toast path raised an exception for this reload request.");
        return false;
    }
}

bool ExtractJsonNumber(const std::string& json, const char* key, double* value) {
    std::string quotedKey = "\"";
    quotedKey += key;
    quotedKey += "\"";

    size_t keyPos = json.find(quotedKey);
    if (keyPos == std::string::npos) return false;

    size_t colonPos = json.find(':', keyPos + quotedKey.size());
    if (colonPos == std::string::npos) return false;

    const char* start = json.c_str() + colonPos + 1;
    char* end = nullptr;
    double parsed = std::strtod(start, &end);
    if (end == start) return false;

    *value = parsed;
    return true;
}

bool ExtractJsonString(const std::string& json, const char* key, std::string* value) {
    std::string quotedKey = "\"";
    quotedKey += key;
    quotedKey += "\"";

    size_t keyPos = json.find(quotedKey);
    if (keyPos == std::string::npos) return false;

    size_t colonPos = json.find(':', keyPos + quotedKey.size());
    if (colonPos == std::string::npos) return false;

    size_t pos = colonPos + 1;
    while (pos < json.size() && std::isspace(static_cast<unsigned char>(json[pos]))) {
        ++pos;
    }
    if (pos >= json.size() || json[pos] != '"') return false;
    ++pos;

    std::string parsed;
    while (pos < json.size()) {
        char ch = json[pos++];
        if (ch == '"') {
            *value = parsed;
            return true;
        }
        if (ch != '\\') {
            parsed.push_back(ch);
            continue;
        }
        if (pos >= json.size()) return false;

        char escaped = json[pos++];
        switch (escaped) {
            case '"': parsed.push_back('"'); break;
            case '\\': parsed.push_back('\\'); break;
            case '/': parsed.push_back('/'); break;
            case 'b': parsed.push_back('\b'); break;
            case 'f': parsed.push_back('\f'); break;
            case 'n': parsed.push_back('\n'); break;
            case 'r': parsed.push_back('\r'); break;
            case 't': parsed.push_back('\t'); break;
            case 'u':
                if (pos + 4 > json.size()) return false;
                parsed.append("\\u");
                parsed.append(json, pos, 4);
                pos += 4;
                break;
            default:
                return false;
        }
    }

    return false;
}

bool ExtractJsonUInt32(const std::string& json, const char* key, uint32_t* value) {
    double numericValue = 0.0;
    if (ExtractJsonNumber(json, key, &numericValue) &&
        numericValue >= 0.0 &&
        numericValue <= 4294967295.0) {

        *value = static_cast<uint32_t>(numericValue);
        return true;
    }

    std::string text;
    if (!ExtractJsonString(json, key, &text)) {
        return false;
    }

    char* end = nullptr;
    unsigned long parsed = std::strtoul(text.c_str(), &end, 0);
    if (end == text.c_str() || *end != '\0' || parsed > 0xFFFFFFFFUL) {
        return false;
    }

    *value = static_cast<uint32_t>(parsed);
    return true;
}

bool LoadWrapperSettingsFromFile(const std::string& wrapperPath) {
    std::string json;
    if (!ReadWholeFile(wrapperPath, &json)) {
        DebugPrint("Wrapper settings: failed to read %s\n", wrapperPath.c_str());
        return false;
    }

    double edgeThreshold = gMOBASkillEdgeThresholdPercent;
    double maxAimBias = gMOBASkillMaxAimXBiasPercent;
    double leftEdge = gMOBASkillLeftEdgeXPercent;
    double rightEdge = gMOBASkillRightEdgeXPercent;
    double debugConsoleEnabled = gDebugConsoleEnabled ? 1.0 : 0.0;
    double customCursorEnabled = gCustomCursorEnabled ? 1.0 : 0.0;
    double imageMarkerProbeEnabled = gImageMarkerProbeEnabled ? 1.0 : 0.0;
    uint32_t imageMarkerProbeColor = gImageMarkerProbeColor;
    std::string customCursorMousePath = gCustomCursorMousePath;
    std::string customCursorMobaPath = gCustomCursorMobaPath;
    std::string customCursorMobaRightPath = gCustomCursorMobaRightPath;
    std::string customCursorBlankPath = gCustomCursorBlankPath;
    std::string imageMarkerProbeLogPath = gImageMarkerProbeLogPath;
    bool foundAny = false;

    foundAny |= ExtractJsonNumber(json, "gDebugConsoleEnabled", &debugConsoleEnabled);
    foundAny |= ExtractJsonNumber(json, "gMOBASkillEdgeThresholdPercent", &edgeThreshold);
    foundAny |= ExtractJsonNumber(json, "gMOBASkillMaxAimXBiasPercent", &maxAimBias);
    foundAny |= ExtractJsonNumber(json, "gMOBASkillLeftEdgeXPercent", &leftEdge);
    foundAny |= ExtractJsonNumber(json, "gMOBASkillRightEdgeXPercent", &rightEdge);
    foundAny |= ExtractJsonNumber(json, "gCustomCursorEnabled", &customCursorEnabled);
    foundAny |= ExtractJsonNumber(json, "gImageMarkerProbeEnabled", &imageMarkerProbeEnabled);
    foundAny |= ExtractJsonUInt32(json, "gImageMarkerProbeColor", &imageMarkerProbeColor);
    foundAny |= ExtractJsonString(json, "gCustomCursorMousePath", &customCursorMousePath);
    foundAny |= ExtractJsonString(json, "gCustomCursorMobaPath", &customCursorMobaPath);
    foundAny |= ExtractJsonString(json, "gCustomCursorMobaRightPath", &customCursorMobaRightPath);
    foundAny |= ExtractJsonString(json, "gCustomCursorBlankPath", &customCursorBlankPath);
    foundAny |= ExtractJsonString(json, "gImageMarkerProbeLogPath", &imageMarkerProbeLogPath);

    if (!foundAny) {
        DebugPrint("Wrapper settings: no known settings found in %s, using defaults\n", wrapperPath.c_str());
        return false;
    }

    gMOBASkillEdgeThresholdPercent = ClampDouble(edgeThreshold, 1.0, 50.0);
    gMOBASkillMaxAimXBiasPercent = ClampDouble(maxAimBias, 0.0, 30.0);
    gMOBASkillLeftEdgeXPercent = ClampDouble(leftEdge, 0.0, 99.0);
    gMOBASkillRightEdgeXPercent = ClampDouble(rightEdge, gMOBASkillLeftEdgeXPercent + 1.0, 100.0);
    gDebugConsoleEnabled = debugConsoleEnabled != 0.0;
    gCustomCursorEnabled = customCursorEnabled != 0.0;
    bool previousImageMarkerProbeEnabled = gImageMarkerProbeEnabled;
    uint32_t previousImageMarkerProbeColor = gImageMarkerProbeColor;
    gImageMarkerProbeEnabled = imageMarkerProbeEnabled != 0.0;
    gImageMarkerProbeColor = imageMarkerProbeColor;
    gCustomCursorMousePath = customCursorMousePath;
    gCustomCursorMobaPath = customCursorMobaPath;
    gCustomCursorMobaRightPath = customCursorMobaRightPath;
    gCustomCursorBlankPath = customCursorBlankPath;
    gImageMarkerProbeLogPath = imageMarkerProbeLogPath;
    if (gImageMarkerProbeEnabled != previousImageMarkerProbeEnabled ||
        gImageMarkerProbeColor != previousImageMarkerProbeColor) {

        InterlockedExchange(&gImageMarkerProbeCallLogCount, 0);
        InterlockedExchange(&gImageMarkerProbeMatchLogCount, 0);
        gImageMarkerProbeLastCallLogTick = 0;
    }
    UpdateDebugConsoleVisibility();
    ReloadCustomCursors();

    DebugPrint(
        "Wrapper settings loaded: debugConsole=%d MOBASkill strength=%.2f edgeWidth=%.2f anchors=(%.2f, %.2f) customCursor=%d mouse=%s moba=%s mobaRight=%s blank=%s\n",
        static_cast<int>(gDebugConsoleEnabled),
        gMOBASkillMaxAimXBiasPercent,
        gMOBASkillEdgeThresholdPercent,
        gMOBASkillLeftEdgeXPercent,
        gMOBASkillRightEdgeXPercent,
        static_cast<int>(gCustomCursorEnabled),
        gCustomCursorMousePath.c_str(),
        gCustomCursorMobaPath.c_str(),
        gCustomCursorMobaRightPath.c_str(),
        gCustomCursorBlankPath.c_str());
    if (gImageMarkerProbeEnabled) {
        DebugPrint(
            "Image marker probe enabled: color=0x%08X log=%s\n",
            gImageMarkerProbeColor,
            GetImageMarkerProbeLogPath().c_str());
        AppendImageMarkerProbeLogLine("=== image marker probe settings loaded ===\n");
    }
    return true;
}

bool LoadWrapperSettings(const std::string& cfgPath) {
    std::string wrapperPath = GetWrapperCfgPath();
    if (GetFileAttributesA(wrapperPath.c_str()) != INVALID_FILE_ATTRIBUTES) {
        return LoadWrapperSettingsFromFile(wrapperPath);
    }

    return LoadWrapperSettingsFromFile(cfgPath);
}

bool LoadAndApplyPackageCfgSafely(
    KmmLoadPackageCfgFn loadPackageCfgFn,
    KmmSetActiveCfgFn setActiveCfgFn,
    KmmDestroyCfgFn destroyCfgFn,
    const std::string& packageName) {

    alignas(16) unsigned char cfgStorage[240] = {};
    bool cfgConstructed = false;

    __try {
        loadPackageCfgFn(cfgStorage, &packageName);
        cfgConstructed = true;

        __int64 applyResult = setActiveCfgFn(cfgStorage, 0);
        if (applyResult != 0) {
            DebugPrint(
                "KMM live reload: SetActiveCfg returned %lld for package=%s\n",
                static_cast<long long>(applyResult),
                packageName.c_str());
        }

        destroyCfgFn(cfgStorage);
        cfgConstructed = false;
        return applyResult == 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ReportHookResolutionError("KMM live cfg apply failed", "The internal cfg load/apply path raised an exception for this reload request.");
        if (cfgConstructed) {
            __try {
                destroyCfgFn(cfgStorage);
            } __except (EXCEPTION_EXECUTE_HANDLER) {
                DebugPrint("KMM live reload: cfg cleanup after failed apply also raised an exception\n");
            }
        }

        return false;
    }
}

bool ReloadPackageCfg(HMODULE hdPlayerModule, const std::string& cfgPath, const std::string& packageName) {
    if (GetFileAttributesA(cfgPath.c_str()) == INVALID_FILE_ATTRIBUTES) {
        DebugPrint("KMM live reload: cfg file does not exist: %s\n", cfgPath.c_str());
        return false;
    }

    if (!IsRvaInsideModule(hdPlayerModule, kKmmLoadPackageCfgRva) ||
        !IsRvaInsideModule(hdPlayerModule, kKmmSetActiveCfgRva) ||
        !IsRvaInsideModule(hdPlayerModule, kKmmDestroyCfgRva) ||
        !IsRvaInsideModule(hdPlayerModule, kKmmSetSchemeByNameRva) ||
        !IsRvaInsideModule(hdPlayerModule, kQtInvokeQVariantMethodRva) ||
        !IsRvaInsideModule(hdPlayerModule, kQStringDtorThunkRva) ||
        !IsRvaInsideModule(hdPlayerModule, kQStringFromStdStringThunkRva) ||
        !IsRvaInsideModule(hdPlayerModule, kQVariantDtorThunkRva) ||
        !IsRvaInsideModule(hdPlayerModule, kQVariantFromQStringThunkRva) ||
        !IsRvaInsideModule(hdPlayerModule, kQVariantMetaTypeInterfaceRva) ||
        !IsRvaInsideModule(hdPlayerModule, kKmmGlobalStatePtrRva)) {
        ReportHookResolutionError("KMM live cfg apply", "One or more KMM reload or SetScheme/toast RVAs are outside the HD-Player image.");
        return false;
    }

    if (packageName.empty()) {
        DebugPrint("KMM live reload: package name is empty for %s\n", cfgPath.c_str());
        return false;
    }

    std::vector<int> setSchemeByNameSig = {
        0x48, 0x89, 0x5c, 0x24, 0x18, 0x55, 0x57, 0x41, 0x56, 0x48,
        0x83, 0xec, 0x40, 0x48, 0x8b, 0xd1, 0x48, 0x8d, 0x4c, 0x24,
        0x20, 0xff, 0x15, 0x35, 0x22, 0xa3, 0x00, 0x48, 0x8b, 0x3d,
        0xbe, 0xef, 0x66, 0x01
    };
    std::vector<int> loadPackageCfgSig = {
        0x48, 0x89, 0x5c, 0x24, -1, 0x48, 0x89, 0x74, 0x24, -1,
        0x57, 0x48, 0x81, 0xec, 0xd0, 0x02, 0x00, 0x00
    };
    std::vector<int> setActiveCfgSig = {
        0x48, 0x89, 0x5c, 0x24, -1, 0x48, 0x89, 0x6c, 0x24, -1,
        0x48, 0x89, 0x74, 0x24, -1, 0x57, 0x48, 0x81, 0xec, 0xe0,
        0x00, 0x00, 0x00, 0x48, 0x8b, 0x05, -1, -1, -1, -1,
        0x48, 0x33, 0xc4, 0x48, 0x89, 0x84, 0x24, -1, -1, -1, -1,
        0x48, 0x8b, 0xf9
    };
    std::vector<int> destroyCfgSig = {
        0x40, 0x57, 0x48, 0x83, 0xec, 0x20, 0x48, 0x8b, 0x91,
        -1, -1, -1, -1, 0x48, 0x8b, 0xf9, 0x48, 0x83, 0xfa, 0x10
    };

    uintptr_t loadPackageCfgAddr = ResolveFunction(hdPlayerModule, loadPackageCfgSig, kKmmLoadPackageCfgRva);
    if (!loadPackageCfgAddr) {
        ReportHookResolutionError("KMM live cfg apply", "LoadPackageCfg signature resolution failed.");
        return false;
    }

    uintptr_t setActiveCfgAddr = ResolveFunction(hdPlayerModule, setActiveCfgSig, kKmmSetActiveCfgRva);
    if (!setActiveCfgAddr) {
        ReportHookResolutionError("KMM live cfg apply", "SetActiveCfg signature resolution failed.");
        return false;
    }

    uintptr_t destroyCfgAddr = ResolveFunction(hdPlayerModule, destroyCfgSig, kKmmDestroyCfgRva);
    if (!destroyCfgAddr) {
        ReportHookResolutionError("KMM live cfg apply", "DestroyCfg signature resolution failed.");
        return false;
    }

    uintptr_t setSchemeByNameAddr = ResolveFunction(hdPlayerModule, setSchemeByNameSig, kKmmSetSchemeByNameRva);
    if (!setSchemeByNameAddr) {
        ReportHookResolutionError("KMM direct scheme switch", "SetScheme signature resolution failed.");
        return false;
    }

    LoadWrapperSettings(cfgPath);

    DebugPrint(
        "KMM live reload: loading and applying package cfg package=%s path=%s\n",
        packageName.c_str(),
        cfgPath.c_str());
    bool applied = LoadAndApplyPackageCfgSafely(
        reinterpret_cast<KmmLoadPackageCfgFn>(loadPackageCfgAddr),
        reinterpret_cast<KmmSetActiveCfgFn>(setActiveCfgAddr),
        reinterpret_cast<KmmDestroyCfgFn>(destroyCfgAddr),
        packageName);
    if (!applied) {
        return false;
    }

    std::string selectedSchemeName;
    if (!ExtractSelectedSchemeNameFromCfg(cfgPath, &selectedSchemeName)) {
        ReportHookResolutionError("KMM direct scheme switch", "The saved cfg did not contain a selected scheme name.");
        return false;
    }

    DebugPrint(
        "KMM live reload: directly selecting scheme package=%s scheme=%s path=%s\n",
        packageName.c_str(),
        selectedSchemeName.c_str(),
        cfgPath.c_str());
    auto setSchemeByNameFn = reinterpret_cast<KmmSetSchemeByNameFn>(setSchemeByNameAddr);
    return SetSelectedSchemeAndToastSafely(hdPlayerModule, setSchemeByNameFn, selectedSchemeName);
}

DWORD WINAPI KmmLiveReloadThread(LPVOID param) {
    HMODULE hdPlayerModule = reinterpret_cast<HMODULE>(param);
    std::vector<ReloadMarkerState> knownReloadMarkers = DiscoverReloadMarkers();

    LoadWrapperSettings(GetBrawlStarsLiveCfgPath());
    DebugPrint("KMM live reload thread started for %s\n", GetLiveUserFilesFolder().c_str());
    DebugPrint("KMM live reload: watching all *.cfg.reload markers\n");

    while (true) {
        std::vector<ReloadMarkerState> currentReloadMarkers = DiscoverReloadMarkers();
        for (const ReloadMarkerState& marker : currentReloadMarkers) {
            const ReloadMarkerState* knownMarker = FindReloadMarkerState(knownReloadMarkers, marker.reloadPath);
            bool editorRequestedReload = knownMarker == nullptr ||
                FileTimesDiffer(knownMarker->writeTime, marker.writeTime);
            if (!editorRequestedReload) {
                continue;
            }

            // Watch only explicit reload request markers, not cfg files.
            // BlueStacks can write cfg files as a side effect of scheme changes.
            RememberEditorSavedCfg(marker.cfgPath, marker.packageName);
            LoadWrapperSettings(marker.cfgPath);
            ReloadPackageCfg(hdPlayerModule, marker.cfgPath, marker.packageName);
            RestoreEditorSavedCfgs();
        }

        knownReloadMarkers = std::move(currentReloadMarkers);
        RestoreEditorSavedCfgs();
        Sleep(250);
    }
}

void LogHookError(const char* step, MH_STATUS status) {
    char buf[256] = {};
    sprintf_s(buf, "BlueStacks dinput8 hook: %s failed with MinHook status %d\n", step, static_cast<int>(status));
    OutputDebugStringA(buf);
    if (gDebugConsoleAttached) {
        printf("%s", buf);
    }
}

DWORD WINAPI MainThread(LPVOID lpReserved) {
    LoadWrapperSettings(GetBrawlStarsLiveCfgPath());
    DebugPrint("Config: save from cfg editor to reload Brawl Stars cfg from BlueStacks user file\n");
    DebugPrint("Initial debugConsole %d, MOBASkill compensation strength %.2f, edge threshold %.2f\n",
        static_cast<int>(gDebugConsoleEnabled),
        gMOBASkillMaxAimXBiasPercent,
        gMOBASkillEdgeThresholdPercent);

    // Signature for BlueStacks Matcher Prologue
    std::vector<int> sig = { 0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x6c, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57, 0x41, 0x56, 0x41, 0x57, 0x48, 0x83, 0xec, 0x20, 0x80, 0x79, 0x2a, 0x00 };

    HMODULE hMod = nullptr;
    uintptr_t targetFunc = 0;
    bool matcherResolutionReported = false;

    while (!targetFunc) {
        hMod = GetModuleHandleA("HD-Player.exe");
        if (hMod) {
            targetFunc = FindPattern(hMod, sig);
            if (!targetFunc && !matcherResolutionReported) {
                matcherResolutionReported = true;
                ReportHookResolutionError("Imgd_FindColorMarkerTriangle", "Matcher signature was not found.");
            }
        }

        if (!targetFunc) {
            Sleep(2000);
        }
    }

    if (targetFunc) {
        CreateThread(nullptr, 0, KmmLiveReloadThread, hMod, 0, nullptr);

        MH_STATUS status = MH_Initialize();
        if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
            LogHookError("MH_Initialize", status);
        }

        status = MH_CreateHook((void*)targetFunc, &CustomMatcher, (reinterpret_cast<void**>(&pOriginalMatcher)));
        if (status != MH_OK) {
            LogHookError("MH_CreateHook CustomMatcher", status);
        } else {
            DebugPrint("CustomMatcher hook created at 0x%p\n", reinterpret_cast<void*>(targetFunc));
        }

        HMODULE user32Module = GetModuleHandleA("user32.dll");
        void* setCursorProc = user32Module ? reinterpret_cast<void*>(GetProcAddress(user32Module, "SetCursor")) : nullptr;
        if (!setCursorProc) {
            ReportHookResolutionError("Custom cursor", "user32!SetCursor could not be resolved.");
        } else {
            status = MH_CreateHook(
                setCursorProc,
                &CustomSetCursor,
                reinterpret_cast<void**>(&pOriginalSetCursor));
            if (status != MH_OK) {
                LogHookError("MH_CreateHook CustomSetCursor", status);
            } else {
                DebugPrint(
                    "Custom cursor hook created at 0x%p enabled=%d\n",
                    setCursorProc,
                    static_cast<int>(gCustomCursorEnabled));
            }
        }

        std::vector<int> applyCursorSig = {
            0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x74,
            0x24, 0x10, 0x48, 0x89, 0x7c, 0x24, 0x18, 0x55,
            0x48, 0x8d, 0x6c, 0x24, 0xe0, 0x48, 0x81, 0xec,
            0x20, 0x01, 0x00, 0x00
        };
        uintptr_t applyCursorFunc = FindPattern(hMod, applyCursorSig);
        if (!applyCursorFunc) {
            applyCursorFunc = ModuleAddressFromRva(hMod, kBlueStacksApplyCursorRva);
            DebugPrint(
                "Custom cursor: applyCursor signature failed; fallback RVA resolved to 0x%p\n",
                reinterpret_cast<void*>(applyCursorFunc));
        }
        if (!applyCursorFunc) {
            ReportHookResolutionError("Custom cursor role detection", "BlueStacks cursor applicator could not be resolved.");
        } else {
            status = MH_CreateHook(
                reinterpret_cast<void*>(applyCursorFunc),
                &CustomBlueStacksApplyCursor,
                reinterpret_cast<void**>(&pOriginalBlueStacksApplyCursor));
            if (status != MH_OK) {
                LogHookError("MH_CreateHook CustomBlueStacksApplyCursor", status);
            } else {
                DebugPrint("Custom cursor role hook created at 0x%p\n", reinterpret_cast<void*>(applyCursorFunc));
            }
        }

        if (IsRvaInsideModule(hMod, kMOBASkillComputeAimCoordsRva)) {
            std::vector<int> aimSig = {
                0x48, 0x8b, 0xc4, 0x55, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57,
                0x48, 0x8d, 0xa8, -1, -1, -1, -1, 0x48, 0x81, 0xec, 0xa8, 0x01, 0x00, 0x00
            };
            uintptr_t aimFunc = ResolveFunction(hMod, aimSig, kMOBASkillComputeAimCoordsRva);
            if (!aimFunc) {
                ReportHookResolutionError("ImapRtMOBASkill_computeAimCoords", "Signature and fallback RVA resolution both failed.");
            } else {
                status = MH_CreateHook(
                    reinterpret_cast<void*>(aimFunc),
                    &CustomMOBASkillComputeAimCoords,
                    reinterpret_cast<void**>(&pOriginalMOBASkillComputeAimCoords));
                if (status != MH_OK) {
                    LogHookError("MH_CreateHook CustomMOBASkillComputeAimCoords", status);
                } else {
                    DebugPrint("MOBASkill aim hook created at 0x%p\n", reinterpret_cast<void*>(aimFunc));
                }
            }
        } else {
            ReportHookResolutionError("ImapRtMOBASkill_computeAimCoords", "Fallback RVA is outside the HD-Player image.");
        }

        status = MH_EnableHook(MH_ALL_HOOKS);
        if (status != MH_OK) {
            LogHookError("MH_EnableHook", status);
        } else {
            DebugPrint("All hooks enabled\n");
        }
    }
    return TRUE;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        gWrapperModuleHandle = hModule;
        InitializeCriticalSection(&gDesiredCfgLock);
        gDesiredCfgLockInitialized = true;
        InitializeCriticalSection(&gCustomCursorLock);
        gCustomCursorLockInitialized = true;
        DisableThreadLibraryCalls(hModule);
        CreateThread(nullptr, 0, MainThread, hModule, 0, nullptr);
    } else if (ul_reason_for_call == DLL_PROCESS_DETACH) {
        if (lpReserved != nullptr) {
            RestoreEditorSavedCfgsOnProcessDetach();
        }
        if (gCustomCursorLockInitialized) {
            EnterCriticalSection(&gCustomCursorLock);
            DestroyCustomCursorsLocked();
            LeaveCriticalSection(&gCustomCursorLock);
            DeleteCriticalSection(&gCustomCursorLock);
            gCustomCursorLockInitialized = false;
        }
    }
    return TRUE;
}
