# BlueStacks HD-Player Offset Update Report

Date: 2026-06-26

## Binary Analyzed

- Input: `C:\Program Files\BlueStacks_nxt\HD-Player.exe`
- IDB: `C:\Users\User\Documents\6.26.2026.HD-Player.exe.i64`
- Image base: `0x140000000`
- SHA-256: `8f7112edc8a525ac7cce07521a46f119e32bde32126f9917302d9a5ad8b3918f`

## Updated Wrapper Targets

| Wrapper target | New RVA | IDA name applied | Signature status |
|---|---:|---|---|
| Imgd color matcher | `0x360570` | `Imgd_FindColorMarkerTriangle` | Existing matcher signature still unique |
| MOBASkill aim compute | `0x3C5690` | `ImapRtMOBASkill_computeAimCoords` | Unique |
| Dpad gamepad analog handler | `0xCED010` | `ImapRtDpad_handleGamepadAnalogMove` | Unique |
| Dpad key event handler | `0xCED320` | `ImapRtDpad_handleKeyEvent` | Unique |
| Dpad virtual joystick updater | `0xCECC70` | `ImapRtDpad_updateVirtualJoystickTouch` | Unique |
| KMM set scheme by name | `0x4029D0` | `Kmm_setSchemeByName` | Unique |
| KMM switch scheme hotkey action | `0x4040A0` | `Kmm_switchSchemeHotkeyAction` | Unique |
| Qt/QML invoke helper | `0x32950` | `Qt_invokeQObjectMethodWithQVariantArgs` | Shared helper, not hooked |
| `QString::~QString` thunk | `0xCDFAAC` | `??1QString@@QEAA@XZ_5` | Import thunk |
| `QString::fromStdString` thunk | `0xCDFAC4` | `?fromStdString@QString@@...` | Import thunk |
| `QVariant::~QVariant` thunk | `0xCDFC08` | `??1QVariant@@QEAA@XZ` | Import thunk |
| `QVariant::QVariant(QString)` thunk | `0xCDFC1A` | `??0QVariant@@QEAA@AEBVQString@@@Z` | Import thunk |

## Signatures Added

The wrapper now resolves targets by byte pattern first. After the load-time crash report, stale RVA fallback was disabled for hooked targets so a missing signature fails closed instead of installing a hook at a merely in-range but possibly wrong address.

- `Imgd_FindColorMarkerTriangle`: kept the existing prologue signature.
- `ImapRtMOBASkill_computeAimCoords`: anchored on the large non-leaf prologue for the aim coordinate routine.
- `ImapRtDpad_handleGamepadAnalogMove`: anchored on the analog handler prologue. This function reads `analogState[5]`, `analogState[6]`, `analogState[7]`, and `analogState[8]`.
- `ImapRtDpad_handleKeyEvent`: anchored on the key-event handler prologue.
- `ImapRtDpad_updateVirtualJoystickTouch`: anchored on the updater prologue.
- `Kmm_setSchemeByName`: anchored on the direct `SetScheme` command implementation. This marks the matching scheme selected by name and applies `qword_141A719B0 + 0x28`.
- `Kmm_switchSchemeHotkeyAction`: anchored on the hotkey action prologue. This is the action registered for `bst.shortcut.switch_scheme`; it was used to recover the toast path, but the wrapper no longer calls it for live reload because it rotates to the next scheme.

## IDA Work Performed

- Renamed the wrapper target functions listed above.
- Applied function prototypes for the MOBASkill, Dpad, and KMM wrapper call contracts.
- Renamed key arguments and locals where useful, including `runtime`, `analogChannel`, `analogState`, `keyEvent`, `eventKind`, `touchActive`, `skillRuntime`, `outAimX`, and `outAimY`.
- Added IDA comments documenting why each target is relevant to the wrapper.
- Used RTTI for `ImapRtDpad`, `ImapRtMOBADpad`, and `ImapRtMOBASkill` to recover vtables and verify related virtual methods.

## Source Changes

Updated:

- `BluestacksCfgEditor/BlueStacksDInputWrapper/main.cpp`

Main changes:

- Updated stale RVAs.
- Added `ResolveFunction`, which tries `FindPattern` and returns null when a signature is not found.
- Added signature-based resolution for MOBASkill, Dpad, and the KMM switch-scheme reload path.
- Added MessageBox-based offset mismatch reporting and null checks before wrapper calls.
- Added SEH guards around hook callback bodies so changed runtime layouts report an offset/layout mismatch instead of crashing `HD-Player.exe`.
- Decoupled editor live save from the wrapper version check. The editor now still writes the live `.cfg` and reload marker even if the installed wrapper is missing or does not match the bundled DLL.
- Replaced the hard-coded Brawl Stars reload watcher with a generic `*.cfg.reload` watcher. The wrapper now derives the package name from the reload marker that changed.
- Hardened editor shutdown by marking the form as closing on `WM_CLOSE`/`SC_CLOSE`, before loaded-field `Leave` commit handlers can display validation UI.
- Removed the `gKmmLiveReloadEnabled` wrapper setting.
- Replaced the crash-prone temporary KMM load/apply/destroy reload path with BlueStacks' direct `SetScheme` command implementation at RVA `0x4029D0`. The wrapper reads the selected scheme name from the saved live `.cfg`, calls `Kmm_setSchemeByName(std::string*)`, and avoids the `Ctrl+Shift+Q` next-scheme rotation.
- Added a small Qt bridge for the BlueStacks scheme toast: the wrapper constructs `QString` and `QVariant` using HD-Player's Qt import thunks, builds the invoke descriptor `{ &unk_141A0D2E0, "QVariant", &variant }`, and calls `Qt_invokeQObjectMethodWithQVariantArgs(*qword_141A719B0, "fShowSchemeChangedToast", args)`.
- Updated the normal Save to live button to mark the current scheme-list selection as the config's `Selected` scheme before writing the live `.cfg`. This makes the button use the same selected-scheme reload behavior as the double-click scheme action.
- Fixed pending editor text not being included in live saves. Save to live and double-click live scheme selection now explicitly commit the pending focused field editor before writing the live `.cfg`; invalid text keeps focus and aborts the save instead of saving stale JSON.
- Fixed the repeated refresh/error caused by committing every visible field during Save to live. The error log showed `Collection was modified; enumeration operation may not execute` in `CommitVisibleFieldEditorsForSave`, because committing fields refreshed the field tables while their dictionaries were being enumerated. The save path now tracks and commits only the pending focused editor.
- Tightened the close-button shutdown guard by marking the form as closing on the title-bar close hit test before field `Leave` handlers can display validation UI.

## On-screen Message Path

- The `Ctrl+Shift+Q` handler calls `Qt_invokeQObjectMethodWithQVariantArgs(*qword_141A719B0, "fShowSchemeChangedToast", args)` after constructing a `QVariant(QString schemeName)`. The QML text is `Control scheme switched to : %1`.
- For later arbitrary messages, the reusable primitive is `QMetaObject::invokeMethodImpl` through `Qt_invokeQObjectMethodWithQVariantArgs` with `QVariant` arguments. Known QML targets:
  - `fShowSchemeChangedToast` on `*qword_141A719B0` for scheme-name toasts.
  - `fShowToast` on the `sidebar` child for generic sidebar toasts.
  - `fShowErrorMessageToast` on the KMM UI object at `*((QWORD*)cfg + 60)` for KMM/editor-style status toasts.
- The wrapper now constructs Qt objects itself for the scheme-changed toast. This gives us a working on-screen message primitive for later features; arbitrary text can reuse the same `QString`/`QVariant` bridge with a different QML target/method.

## Verification

- IDA uniqueness checks:
  - Matcher: `0x360570`
  - MOBASkill aim compute: `0x3C5690`
  - Dpad analog: `0xCED010`
  - Dpad update: `0xCECC70`
  - Dpad key: `0xCED320`
  - KMM set scheme by name: `0x4029D0`
  - KMM switch scheme hotkey action: `0x4040A0`
- IDA message-path recovery:
  - `Kmm_switchSchemeHotkeyAction` calls `Kmm_setActiveCfg(qword_141A719B0 + 0x28, 1)` at `0x14040429D`.
  - It invokes `fShowSchemeChangedToast` at `0x14040434C`.
  - `Qt_invokeQObjectMethodWithQVariantArgs` wraps `QMetaObject::invokeMethodImpl` at `0x140032950`.
  - `Kmm_setSchemeByName` directly selects by `std::string` scheme name, updates the selected index, then calls `Kmm_setActiveCfg(qword_141A719B0 + 0x28, 1)` at `0x140402AAB`.
- Built the native wrapper successfully with MSBuild:
  - Project: `BlueStacksDInputWrapper/BlueStacksDInputWrapper.vcxproj`
  - Configuration: `Release`
  - Platform: `x64`
  - Result: 0 warnings, 0 errors
- Rebuilt after the live-reload crash fix:
  - Output: `BlueStacksDInputWrapper/x64/Release/dinput8.dll`
  - Copied installer-facing DLL: `dinput8.dll`
  - Size: `73216` bytes
- Rebuilt after restoring live-save apply:
  - Native wrapper: 0 warnings, 0 errors
  - Editor: 0 warnings, 0 errors
  - New wrapper SHA-256 starts with `31E705AED0ABC2F3`
- Rebuilt after generic live reload watcher and close fix:
  - Native wrapper: 0 warnings, 0 errors
  - Editor: 0 warnings, 0 errors
  - New wrapper size: `84480` bytes
  - New wrapper SHA-256 starts with `736DFB16AC7FD3FB`
- Rebuilt after switching live reload markers to the `Ctrl+Shift+Q` scheme path:
  - Native wrapper: 0 warnings, 0 errors
  - Editor: 0 warnings, 0 errors
  - Output copied to installer-facing `dinput8.dll`
  - New wrapper size: `81920` bytes
  - New wrapper SHA-256: `E629D8CBA140B9816282A43C3939698179354B350AB97F529E2DD8F4B466965C`
- Rebuilt after changing live reload to direct selected-scheme switching plus explicit toast drawing:
  - Native wrapper: 0 warnings, 0 errors
  - Editor: 0 warnings, 0 errors
  - Output copied to installer-facing `dinput8.dll`
  - New wrapper size: `88064` bytes
  - New wrapper SHA-256: `FFB8EE8C27FDCCE6613660B25FEFA0D3481F2A6EC0FC3CBD1531BA1BAD2CD6F5`
- Rebuilt after updating the Save to live button to select the current scheme before saving:
  - Editor: 0 warnings, 0 errors
- Rebuilt after fixing pending field commits and title-bar close handling:
  - Editor: 0 warnings, 0 errors
- Rebuilt after replacing bulk visible-field commits with pending focused-field commits:
  - Editor: 0 warnings, 0 errors

## Notes

- The existing color matcher prologue signature survived this BlueStacks update.
- The old Dpad RVAs now landed in unrelated code. The new Dpad targets were derived from `ImapRtDpad` RTTI/vtable analysis and confirmed by decompilation behavior.
- `std::string` was not available as an IDA parser type in this database, so the `Kmm_loadPackageCfg` package-name argument was documented as a pointer in IDA while preserving the wrapper's source typedef.
