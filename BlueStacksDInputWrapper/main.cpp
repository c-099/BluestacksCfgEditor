#include <windows.h>
#include <cstdint>
#include <vector>
#include <psapi.h>
#include <MinHook.h>
#include <cstdio>
#include <algorithm>
#include <cstdarg>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <string>

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

typedef void(*MOBASkillComputeAimCoordsFn)(void*, int, int, double*, double*, char);
MOBASkillComputeAimCoordsFn pOriginalMOBASkillComputeAimCoords = nullptr;

typedef __int64(__fastcall *DpadHandleGamepadAnalogMoveFn)(void*, int, float*);
DpadHandleGamepadAnalogMoveFn pOriginalDpadHandleGamepadAnalogMove = nullptr;

typedef __int64(__fastcall *DpadHandleKeyEventFn)(void*, __int64, int);
DpadHandleKeyEventFn pOriginalDpadHandleKeyEvent = nullptr;

typedef __int64(__fastcall *DpadUpdateVirtualJoystickTouchFn)(void*, char);
DpadUpdateVirtualJoystickTouchFn pDpadUpdateVirtualJoystickTouch = nullptr;

// Replacement for HD-Player's Imgd color marker matcher.
// This intentionally keeps the working runtime behavior from the original
// patch, even where it differs from the cleaner Ghidra decompile.
uint8_t CustomMatcher(ImgdState* state, int mode, short* outIdx, int p4, uint32_t mc) {
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

// ========================================================================
// 3. BRAWL STARS MOBASKILL AIM COMPENSATION
// ========================================================================
constexpr uintptr_t kMOBASkillComputeAimCoordsRva = 0x39B530;
constexpr uintptr_t kDpadHandleGamepadAnalogMoveRva = 0xCC1A80;
constexpr uintptr_t kDpadHandleKeyEventRva = 0xCC1D90;
constexpr uintptr_t kDpadUpdateVirtualJoystickTouchRva = 0xCC16E0;
constexpr double kMOBASkillScreenPercentMax = 100.0;
double gMOBASkillEdgeThresholdPercent = 25.0;
double gMOBASkillMaxAimXBiasPercent = 1.5;
DWORD gMOBASkillLastDebugPrintTick = 0;
double gMOBASkillLeftEdgeXPercent = 9.8;
double gMOBASkillRightEdgeXPercent = 90.2;
double gMOBASkillAimYBiasScaleStartPercent = 12.0;
double gMOBASkillAimYBiasScaleEndPercent = 30.0;
double gMOBASkillAimYBiasMinScale = -1.0;
bool gDpadFullExtensionEnabled = true;
double gDpadDirectionSmoothingFactor = 0.45;
double gDpadZeroHoldMs = 80.0;
double gDpadKeyboardZeroHoldMs = 120.0;
DWORD gDpadLastDebugPrintTick = 0;
DWORD gDpadKeyboardLastDebugPrintTick = 0;
bool gDebugConsoleEnabled = false;
bool gDebugConsoleAttached = false;

struct DpadStickSmoothingState {
    bool hasDirection = false;
    double x = 0.0;
    double y = 0.0;
    DWORD lastInputTick = 0;
};

DpadStickSmoothingState gDpadLeftStickState;
DpadStickSmoothingState gDpadRightStickState;

struct DpadKeyboardHoldState {
    void* runtime = nullptr;
    bool hasDirection = false;
    double xOffset = 0.0;
    double yOffset = 0.0;
    DWORD lastInputTick = 0;
};

DpadKeyboardHoldState gDpadKeyboardHoldState;
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

void ResetDpadSmoothingState() {
    gDpadLeftStickState = {};
    gDpadRightStickState = {};
    gDpadKeyboardHoldState = {};
}

bool NormalizeAndSmoothAnalogPair(
    float* analogState,
    size_t xIndex,
    size_t yIndex,
    DpadStickSmoothingState* state,
    DWORD now,
    const char** mode) {

    *mode = "none";
    double x = analogState[xIndex];
    double y = analogState[yIndex];
    double length = std::sqrt((x * x) + (y * y));
    if (!std::isfinite(length)) {
        state->hasDirection = false;
        *mode = "reset";
        return false;
    }

    if (length <= 0.0001) {
        DWORD elapsed = now - state->lastInputTick;
        if (state->hasDirection && elapsed <= static_cast<DWORD>(gDpadZeroHoldMs)) {
            analogState[xIndex] = static_cast<float>(state->x);
            analogState[yIndex] = static_cast<float>(state->y);
            *mode = "hold";
            return true;
        }

        state->hasDirection = false;
        return false;
    }

    double targetX = x / length;
    double targetY = y / length;
    double outputX = targetX;
    double outputY = targetY;
    double smoothingFactor = ClampDouble(gDpadDirectionSmoothingFactor, 0.0, 1.0);

    if (state->hasDirection && smoothingFactor < 1.0) {
        outputX = state->x + ((targetX - state->x) * smoothingFactor);
        outputY = state->y + ((targetY - state->y) * smoothingFactor);
        double outputLength = std::sqrt((outputX * outputX) + (outputY * outputY));
        if (std::isfinite(outputLength) && outputLength > 0.0001) {
            outputX /= outputLength;
            outputY /= outputLength;
            *mode = "smooth";
        } else {
            outputX = targetX;
            outputY = targetY;
            *mode = "snap";
        }
    } else {
        *mode = "snap";
    }

    state->hasDirection = true;
    state->x = outputX;
    state->y = outputY;
    state->lastInputTick = now;
    analogState[xIndex] = static_cast<float>(outputX);
    analogState[yIndex] = static_cast<float>(outputY);
    return true;
}

// Hook for HD-Player's normal ImapRtDpad gamepad analog movement handler.
// BlueStacks normally scales the virtual joystick by raw analog magnitude.
// Brawl Stars movement needs the stick endpoint on the outer circle whenever
// movement is nonzero, so this normalizes the temporary analog state before the
// original handler computes and emits the virtual touch.
__int64 __fastcall CustomDpadHandleGamepadAnalogMove(
    void* runtime,
    int analogChannel,
    float* analogState) {

    if (!pOriginalDpadHandleGamepadAnalogMove) return 0;
    if (!gDpadFullExtensionEnabled || !analogState) {
        return pOriginalDpadHandleGamepadAnalogMove(runtime, analogChannel, analogState);
    }

    float normalizedAnalogState[9] = {};
    std::memcpy(normalizedAnalogState, analogState, sizeof(normalizedAnalogState));

    DWORD now = GetTickCount();
    const char* leftMode = "none";
    const char* rightMode = "none";
    bool changedLeft = NormalizeAndSmoothAnalogPair(
        normalizedAnalogState,
        5,
        6,
        &gDpadLeftStickState,
        now,
        &leftMode);
    bool changedRight = NormalizeAndSmoothAnalogPair(
        normalizedAnalogState,
        7,
        8,
        &gDpadRightStickState,
        now,
        &rightMode);

    if ((changedLeft || changedRight) && now - gDpadLastDebugPrintTick >= 250) {
        gDpadLastDebugPrintTick = now;
        DebugPrint(
            "Dpad full-extension active channel=%d smoothing=%.2f zeroHoldMs=%.0f left[%s]=(%.3f, %.3f)->(%.3f, %.3f) right[%s]=(%.3f, %.3f)->(%.3f, %.3f)\n",
            analogChannel,
            gDpadDirectionSmoothingFactor,
            gDpadZeroHoldMs,
            leftMode,
            analogState[5],
            analogState[6],
            normalizedAnalogState[5],
            normalizedAnalogState[6],
            rightMode,
            analogState[7],
            analogState[8],
            normalizedAnalogState[7],
            normalizedAnalogState[8]);
    }

    return pOriginalDpadHandleGamepadAnalogMove(runtime, analogChannel, normalizedAnalogState);
}

bool IsNonZeroDpadOffset(double xOffset, double yOffset) {
    return std::fabs(xOffset) > 0.0001 || std::fabs(yOffset) > 0.0001;
}

// Hook for HD-Player's normal ImapRtDpad keyboard path. The original handler
// already computes full-radius offsets for WASD directions, so this only
// bridges brief zero-direction samples that can happen while changing keys.
__int64 __fastcall CustomDpadHandleKeyEvent(
    void* runtime,
    __int64 keyEvent,
    int eventKind) {

    if (!pOriginalDpadHandleKeyEvent) return 0;

    __int64 result = pOriginalDpadHandleKeyEvent(runtime, keyEvent, eventKind);
    if (!gDpadFullExtensionEnabled || !runtime || !pDpadUpdateVirtualJoystickTouch) {
        return result;
    }

    auto* runtimeBytes = reinterpret_cast<uint8_t*>(runtime);
    double* xOffsetPtr = reinterpret_cast<double*>(runtimeBytes + 0x118);
    double* yOffsetPtr = reinterpret_cast<double*>(runtimeBytes + 0x120);
    double xOffset = *xOffsetPtr;
    double yOffset = *yOffsetPtr;
    DWORD now = GetTickCount();

    if (IsNonZeroDpadOffset(xOffset, yOffset)) {
        gDpadKeyboardHoldState.runtime = runtime;
        gDpadKeyboardHoldState.hasDirection = true;
        gDpadKeyboardHoldState.xOffset = xOffset;
        gDpadKeyboardHoldState.yOffset = yOffset;
        gDpadKeyboardHoldState.lastInputTick = now;
        return result;
    }

    DWORD elapsed = now - gDpadKeyboardHoldState.lastInputTick;
    if (gDpadKeyboardHoldState.hasDirection &&
        gDpadKeyboardHoldState.runtime == runtime &&
        elapsed <= static_cast<DWORD>(gDpadKeyboardZeroHoldMs)) {

        *xOffsetPtr = gDpadKeyboardHoldState.xOffset;
        *yOffsetPtr = gDpadKeyboardHoldState.yOffset;
        result = pDpadUpdateVirtualJoystickTouch(runtime, 1);

        if (now - gDpadKeyboardLastDebugPrintTick >= 250) {
            gDpadKeyboardLastDebugPrintTick = now;
            DebugPrint(
                "Dpad keyboard zero-bridge active eventKind=%d elapsed=%lu zeroHoldMs=%.0f heldOffset=(%.3f, %.3f)\n",
                eventKind,
                static_cast<unsigned long>(elapsed),
                gDpadKeyboardZeroHoldMs,
                gDpadKeyboardHoldState.xOffset,
                gDpadKeyboardHoldState.yOffset);
        }
    } else if (elapsed > static_cast<DWORD>(gDpadKeyboardZeroHoldMs)) {
        gDpadKeyboardHoldState.hasDirection = false;
    }

    return result;
}

// Hook for HD-Player's ImapMOBASkillComputeAimCoords. The original function
// computes the virtual skill-stick endpoint from mouse coordinates. After the
// original math runs, this nudges only the X endpoint when the character is
// close to a horizontal screen edge. Near the left edge, compensate left;
// near the right edge, compensate right.
void CustomMOBASkillComputeAimCoords(
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
    double aimYScale = 1.0;
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
        // Scale by shot direction, not absolute screen Y. This keeps edge
        // compensation stable across maps/control origins. Positive Y is
        // downward, where the horizontal edge correction needs to reverse.
        if (aimDeltaYPercent > 0.0) {
            aimYScale = gMOBASkillAimYBiasMinScale;
        } else if (aimDeltaYPercent > gMOBASkillAimYBiasScaleStartPercent) {
            double scaleRange = gMOBASkillAimYBiasScaleEndPercent - gMOBASkillAimYBiasScaleStartPercent;
            if (scaleRange > 0.0) {
                double t = (aimDeltaYPercent - gMOBASkillAimYBiasScaleStartPercent) / scaleRange;
                t = ClampDouble(t, 0.0, 1.0);
                aimYScale = 1.0 - (t * (1.0 - gMOBASkillAimYBiasMinScale));
            }
        }
        xBias *= aimYScale;

        // Do not clamp the aim endpoint to 0..100. The original function can
        // intentionally return values outside screen percent bounds for long
        // aim vectors, and clamping here changes the shot direction.
        *outAimX = *outAimX + xBias;
    }

    DWORD now = GetTickCount();
    if (now - gMOBASkillLastDebugPrintTick >= 100) {
        gMOBASkillLastDebugPrintTick = now;
        DebugPrint(
            "MOBASkill pos=(%.2f, %.2f) origin=(%.2f, %.2f) aimDelta=(%.2f, %.2f) edgeAnchors=(%.2f, %.2f) edgeZones=(%.2f..%.2f, %.2f..%.2f) mouse=(%d,%d) aim=(%.2f, %.2f)->(%.2f, %.2f) closeness=%.2f aimYScale=%.2f xBias=%.2f strength=%.2f edgeWidth=%.2f deadzone=%d\n",
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
            aimYScale,
            xBias,
            gMOBASkillMaxAimXBiasPercent,
            gMOBASkillEdgeThresholdPercent,
            static_cast<int>(clampToDeadzone));
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

bool IsRvaInsideModule(HMODULE hMod, uintptr_t rva) {
    if (!hMod) return false;

    MODULEINFO info = {0};
    if (!GetModuleInformation(GetCurrentProcess(), hMod, &info, sizeof(info))) return false;

    return info.lpBaseOfDll && rva < info.SizeOfImage;
}

// ========================================================================
// 4. LIVE KMM CFG RELOAD
// ========================================================================
constexpr uintptr_t kKmmImportSchemesFromJsonPayloadRva = 0x404C10;
constexpr uintptr_t kKmmLoadPackageCfgRva = 0x3D1200;
constexpr uintptr_t kKmmSetActiveCfgRva = 0x3D84B0;
constexpr uintptr_t kKmmDestroyCfgRva = 0x3C9B60;
constexpr const char* kBrawlStarsPackageName = "com.supercell.brawlstars";
constexpr const char* kQByteArrayCtorDataSize =
    "??0QByteArray@@QEAA@PEBD_J@Z";

using QByteArrayCtorDataSizeFn = void* (*)(void*, const char*, long long);
using KmmImportSchemesFromJsonPayloadFn = void (*)(void*, std::string*);
using KmmLoadPackageCfgFn = void* (*)(void*, std::string*);
using KmmSetActiveCfgFn = uint32_t (*)(void*, char);
using KmmDestroyCfgFn = void (*)(void*);

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

std::string GetWrapperCfgPath() {
    std::string root = GetBlueStacksDataRoot();
    while (!root.empty() && (root.back() == '\\' || root.back() == '/')) {
        root.pop_back();
    }
    return root + "\\Engine\\UserData\\dinput8-config.json";
}

std::string GetBrawlStarsReloadRequestPath() {
    return GetBrawlStarsLiveCfgPath() + ".reload";
}

bool ReadWholeFile(const std::string& path, std::string* contents) {
    std::ifstream input(path, std::ios::binary);
    if (!input) return false;
    contents->assign(std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>());
    return !contents->empty();
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
    double aimYScaleStart = gMOBASkillAimYBiasScaleStartPercent;
    double aimYScaleEnd = gMOBASkillAimYBiasScaleEndPercent;
    double aimYMinScale = gMOBASkillAimYBiasMinScale;
    double dpadFullExtensionEnabled = gDpadFullExtensionEnabled ? 1.0 : 0.0;
    double dpadDirectionSmoothingFactor = gDpadDirectionSmoothingFactor;
    double dpadZeroHoldMs = gDpadZeroHoldMs;
    double dpadKeyboardZeroHoldMs = gDpadKeyboardZeroHoldMs;
    double debugConsoleEnabled = gDebugConsoleEnabled ? 1.0 : 0.0;
    bool foundAny = false;

    foundAny |= ExtractJsonNumber(json, "gDebugConsoleEnabled", &debugConsoleEnabled);
    foundAny |= ExtractJsonNumber(json, "gMOBASkillEdgeThresholdPercent", &edgeThreshold);
    foundAny |= ExtractJsonNumber(json, "gMOBASkillMaxAimXBiasPercent", &maxAimBias);
    foundAny |= ExtractJsonNumber(json, "gMOBASkillLeftEdgeXPercent", &leftEdge);
    foundAny |= ExtractJsonNumber(json, "gMOBASkillRightEdgeXPercent", &rightEdge);
    foundAny |= ExtractJsonNumber(json, "gMOBASkillAimYBiasScaleStartPercent", &aimYScaleStart);
    foundAny |= ExtractJsonNumber(json, "gMOBASkillAimYBiasScaleEndPercent", &aimYScaleEnd);
    bool foundAimYMinScale = ExtractJsonNumber(json, "gMOBASkillAimYBiasMinScale", &aimYMinScale);
    foundAny |= foundAimYMinScale;
    foundAny |= ExtractJsonNumber(json, "gDpadFullExtensionEnabled", &dpadFullExtensionEnabled);
    bool foundDpadDirectionSmoothingFactor = ExtractJsonNumber(json, "gDpadDirectionSmoothingFactor", &dpadDirectionSmoothingFactor);
    foundAny |= foundDpadDirectionSmoothingFactor;
    foundAny |= ExtractJsonNumber(json, "gDpadZeroHoldMs", &dpadZeroHoldMs);
    foundAny |= ExtractJsonNumber(json, "gDpadKeyboardZeroHoldMs", &dpadKeyboardZeroHoldMs);

    if (!foundAny) {
        DebugPrint("Wrapper settings: no known settings found in %s, using defaults\n", wrapperPath.c_str());
        return false;
    }

    gMOBASkillEdgeThresholdPercent = ClampDouble(edgeThreshold, 1.0, 50.0);
    gMOBASkillMaxAimXBiasPercent = ClampDouble(maxAimBias, 0.0, 30.0);
    gMOBASkillLeftEdgeXPercent = ClampDouble(leftEdge, 0.0, 99.0);
    gMOBASkillRightEdgeXPercent = ClampDouble(rightEdge, gMOBASkillLeftEdgeXPercent + 1.0, 100.0);
    gMOBASkillAimYBiasScaleStartPercent = ClampDouble(aimYScaleStart, 0.0, 100.0);
    gMOBASkillAimYBiasScaleEndPercent = ClampDouble(
        aimYScaleEnd,
        gMOBASkillAimYBiasScaleStartPercent + 1.0,
        100.0);
    gMOBASkillAimYBiasMinScale = ClampDouble(aimYMinScale, -1.0, 1.0);
    if (foundAimYMinScale && gMOBASkillAimYBiasMinScale >= 0.0) {
        DebugPrint(
            "Wrapper settings: migrating legacy non-reversing gMOBASkillAimYBiasMinScale %.2f to -1.00\n",
            gMOBASkillAimYBiasMinScale);
        gMOBASkillAimYBiasMinScale = -1.0;
    }
    gDpadFullExtensionEnabled = dpadFullExtensionEnabled != 0.0;
    if (foundDpadDirectionSmoothingFactor &&
        (dpadDirectionSmoothingFactor < 0.0 || dpadDirectionSmoothingFactor > 1.0)) {
        DebugPrint(
            "Wrapper settings: migrating out-of-range gDpadDirectionSmoothingFactor %.2f to 0.45\n",
            dpadDirectionSmoothingFactor);
        dpadDirectionSmoothingFactor = 0.45;
    }
    gDpadDirectionSmoothingFactor = ClampDouble(dpadDirectionSmoothingFactor, 0.0, 1.0);
    gDpadZeroHoldMs = ClampDouble(dpadZeroHoldMs, 0.0, 250.0);
    gDpadKeyboardZeroHoldMs = ClampDouble(dpadKeyboardZeroHoldMs, 0.0, 250.0);
    gDebugConsoleEnabled = debugConsoleEnabled != 0.0;
    UpdateDebugConsoleVisibility();
    if (!gDpadFullExtensionEnabled) {
        ResetDpadSmoothingState();
    }

    DebugPrint(
        "Wrapper settings loaded: debugConsole=%d MOBASkill strength=%.2f edgeWidth=%.2f anchors=(%.2f, %.2f) downwardAimYScale=(%.2f..%.2f min %.2f) dpadFullExtension=%d dpadSmoothing=%.2f dpadZeroHoldMs=%.0f dpadKeyboardZeroHoldMs=%.0f\n",
        static_cast<int>(gDebugConsoleEnabled),
        gMOBASkillMaxAimXBiasPercent,
        gMOBASkillEdgeThresholdPercent,
        gMOBASkillLeftEdgeXPercent,
        gMOBASkillRightEdgeXPercent,
        gMOBASkillAimYBiasScaleStartPercent,
        gMOBASkillAimYBiasScaleEndPercent,
        gMOBASkillAimYBiasMinScale,
        static_cast<int>(gDpadFullExtensionEnabled),
        gDpadDirectionSmoothingFactor,
        gDpadZeroHoldMs,
        gDpadKeyboardZeroHoldMs);
    return true;
}

bool LoadWrapperSettings(const std::string& cfgPath) {
    std::string wrapperPath = GetWrapperCfgPath();
    if (GetFileAttributesA(wrapperPath.c_str()) != INVALID_FILE_ATTRIBUTES) {
        return LoadWrapperSettingsFromFile(wrapperPath);
    }

    return LoadWrapperSettingsFromFile(cfgPath);
}

bool ReloadBrawlStarsCfg(HMODULE hdPlayerModule, const std::string& cfgPath) {
    if (GetFileAttributesA(cfgPath.c_str()) == INVALID_FILE_ATTRIBUTES) {
        DebugPrint("KMM live reload: cfg file does not exist: %s\n", cfgPath.c_str());
        return false;
    }

    if (!IsRvaInsideModule(hdPlayerModule, kKmmLoadPackageCfgRva) ||
        !IsRvaInsideModule(hdPlayerModule, kKmmSetActiveCfgRva) ||
        !IsRvaInsideModule(hdPlayerModule, kKmmDestroyCfgRva)) {
        DebugPrint("KMM live reload: one or more KMM reload RVAs are outside HD-Player image\n");
        return false;
    }

    std::string packageName = kBrawlStarsPackageName;
    auto loadPackageCfgFn = reinterpret_cast<KmmLoadPackageCfgFn>(
        reinterpret_cast<uintptr_t>(hdPlayerModule) + kKmmLoadPackageCfgRva);
    auto setActiveCfgFn = reinterpret_cast<KmmSetActiveCfgFn>(
        reinterpret_cast<uintptr_t>(hdPlayerModule) + kKmmSetActiveCfgRva);
    auto destroyCfgFn = reinterpret_cast<KmmDestroyCfgFn>(
        reinterpret_cast<uintptr_t>(hdPlayerModule) + kKmmDestroyCfgRva);

    alignas(16) unsigned char cfgStorage[240] = {};
    LoadWrapperSettings(cfgPath);
    DebugPrint("KMM live reload: loading package cfg from disk: %s\n", cfgPath.c_str());
    loadPackageCfgFn(cfgStorage, &packageName);
    uint32_t result = setActiveCfgFn(cfgStorage, 0);
    destroyCfgFn(cfgStorage);
    DebugPrint("KMM live reload: KmmSetActiveCfg returned 0x%x\n", result);
    return result == 0;
}

DWORD WINAPI KmmLiveReloadThread(LPVOID param) {
    HMODULE hdPlayerModule = reinterpret_cast<HMODULE>(param);
    std::string cfgPath = GetBrawlStarsLiveCfgPath();
    std::string reloadRequestPath = GetBrawlStarsReloadRequestPath();
    FILETIME lastReloadRequestTime = {};
    bool haveReloadRequestTime = GetFileWriteTime(reloadRequestPath, &lastReloadRequestTime);

    LoadWrapperSettings(cfgPath);
    DebugPrint("KMM live reload thread started for %s\n", cfgPath.c_str());
    DebugPrint("KMM live reload: save from cfg editor to reload from disk\n");

    while (true) {
        FILETIME currentReloadRequestTime = {};
        bool editorRequestedReload = false;
        if (GetFileWriteTime(reloadRequestPath, &currentReloadRequestTime)) {
            if (!haveReloadRequestTime) {
                lastReloadRequestTime = currentReloadRequestTime;
                haveReloadRequestTime = true;
                editorRequestedReload = true;
            } else if (FileTimesDiffer(lastReloadRequestTime, currentReloadRequestTime)) {
                lastReloadRequestTime = currentReloadRequestTime;
                editorRequestedReload = true;
            }
        }

        // Watch only the explicit reload request marker, not the cfg file. The
        // KMM apply path can write the cfg file as a side effect.
        if (editorRequestedReload) {
            ReloadBrawlStarsCfg(hdPlayerModule, cfgPath);
        }

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
    DebugPrint("Initial debugConsole %d, MOBASkill compensation strength %.2f, edge threshold %.2f, Dpad full-extension %d smoothing %.2f analogZeroHoldMs %.0f keyboardZeroHoldMs %.0f\n",
        static_cast<int>(gDebugConsoleEnabled),
        gMOBASkillMaxAimXBiasPercent,
        gMOBASkillEdgeThresholdPercent,
        static_cast<int>(gDpadFullExtensionEnabled),
        gDpadDirectionSmoothingFactor,
        gDpadZeroHoldMs,
        gDpadKeyboardZeroHoldMs);

    // Signature for BlueStacks Matcher Prologue
    std::vector<int> sig = { 0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x6c, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57, 0x41, 0x56, 0x41, 0x57, 0x48, 0x83, 0xec, 0x20, 0x80, 0x79, 0x2a, 0x00 };

    HMODULE hMod = nullptr;
    uintptr_t targetFunc = 0;

    while (!targetFunc) {
        hMod = GetModuleHandleA("HD-Player.exe");
        if (hMod) {
            targetFunc = FindPattern(hMod, sig);
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

        if (IsRvaInsideModule(hMod, kMOBASkillComputeAimCoordsRva)) {
            uintptr_t aimFunc = reinterpret_cast<uintptr_t>(hMod) + kMOBASkillComputeAimCoordsRva;
            status = MH_CreateHook(
                reinterpret_cast<void*>(aimFunc),
                &CustomMOBASkillComputeAimCoords,
                reinterpret_cast<void**>(&pOriginalMOBASkillComputeAimCoords));
            if (status != MH_OK) {
                LogHookError("MH_CreateHook CustomMOBASkillComputeAimCoords", status);
            } else {
                DebugPrint("MOBASkill aim hook created at 0x%p\n", reinterpret_cast<void*>(aimFunc));
            }
        } else {
            OutputDebugStringA("BlueStacks dinput8 hook: MOBASkill RVA is outside HD-Player image\n");
        }

        if (IsRvaInsideModule(hMod, kDpadHandleGamepadAnalogMoveRva)) {
            uintptr_t dpadAnalogFunc = reinterpret_cast<uintptr_t>(hMod) + kDpadHandleGamepadAnalogMoveRva;
            status = MH_CreateHook(
                reinterpret_cast<void*>(dpadAnalogFunc),
                &CustomDpadHandleGamepadAnalogMove,
                reinterpret_cast<void**>(&pOriginalDpadHandleGamepadAnalogMove));
            if (status != MH_OK) {
                LogHookError("MH_CreateHook CustomDpadHandleGamepadAnalogMove", status);
            } else {
                DebugPrint(
                    "Dpad analog full-extension hook created at 0x%p enabled=%d smoothing=%.2f zeroHoldMs=%.0f\n",
                    reinterpret_cast<void*>(dpadAnalogFunc),
                    static_cast<int>(gDpadFullExtensionEnabled),
                    gDpadDirectionSmoothingFactor,
                    gDpadZeroHoldMs);
            }
        } else {
            OutputDebugStringA("BlueStacks dinput8 hook: Dpad analog RVA is outside HD-Player image\n");
        }

        if (IsRvaInsideModule(hMod, kDpadUpdateVirtualJoystickTouchRva)) {
            pDpadUpdateVirtualJoystickTouch = reinterpret_cast<DpadUpdateVirtualJoystickTouchFn>(
                reinterpret_cast<uintptr_t>(hMod) + kDpadUpdateVirtualJoystickTouchRva);
            DebugPrint("Dpad virtual joystick update function resolved at 0x%p\n", reinterpret_cast<void*>(pDpadUpdateVirtualJoystickTouch));
        } else {
            OutputDebugStringA("BlueStacks dinput8 hook: Dpad update RVA is outside HD-Player image\n");
        }

        if (IsRvaInsideModule(hMod, kDpadHandleKeyEventRva)) {
            uintptr_t dpadKeyFunc = reinterpret_cast<uintptr_t>(hMod) + kDpadHandleKeyEventRva;
            status = MH_CreateHook(
                reinterpret_cast<void*>(dpadKeyFunc),
                &CustomDpadHandleKeyEvent,
                reinterpret_cast<void**>(&pOriginalDpadHandleKeyEvent));
            if (status != MH_OK) {
                LogHookError("MH_CreateHook CustomDpadHandleKeyEvent", status);
            } else {
                DebugPrint(
                    "Dpad keyboard zero-bridge hook created at 0x%p enabled=%d zeroHoldMs=%.0f\n",
                    reinterpret_cast<void*>(dpadKeyFunc),
                    static_cast<int>(gDpadFullExtensionEnabled),
                    gDpadKeyboardZeroHoldMs);
            }
        } else {
            OutputDebugStringA("BlueStacks dinput8 hook: Dpad key RVA is outside HD-Player image\n");
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
        DisableThreadLibraryCalls(hModule);
        CreateThread(nullptr, 0, MainThread, hModule, 0, nullptr);
    }
    return TRUE;
}
