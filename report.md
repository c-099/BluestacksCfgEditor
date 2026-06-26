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
| KMM load package cfg | `0x3F9F80` | `Kmm_loadPackageCfg` | Unique after extended pattern |
| KMM set active cfg | `0x402610` | `Kmm_setActiveCfg` | Unique after extended pattern |
| KMM destroy cfg | `0x3F3CC0` | `Kmm_destroyCfg` | RVA fallback retained |

## Signatures Added

The wrapper now resolves targets by byte pattern first. After the load-time crash report, stale RVA fallback was disabled for hooked targets so a missing signature fails closed instead of installing a hook at a merely in-range but possibly wrong address.

- `Imgd_FindColorMarkerTriangle`: kept the existing prologue signature.
- `ImapRtMOBASkill_computeAimCoords`: anchored on the large non-leaf prologue for the aim coordinate routine.
- `ImapRtDpad_handleGamepadAnalogMove`: anchored on the analog handler prologue. This function reads `analogState[5]`, `analogState[6]`, `analogState[7]`, and `analogState[8]`.
- `ImapRtDpad_handleKeyEvent`: anchored on the key-event handler prologue.
- `ImapRtDpad_updateVirtualJoystickTouch`: anchored on the updater prologue.
- `Kmm_loadPackageCfg` and `Kmm_setActiveCfg`: use longer signatures because shorter logging-style prologues matched multiple KMM functions.

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
- Added signature-based resolution for MOBASkill, Dpad, and the KMM reload path.
- Added MessageBox-based offset mismatch reporting and null checks before wrapper calls.
- Added SEH guards around hook callback bodies so changed runtime layouts report an offset/layout mismatch instead of crashing `HD-Player.exe`.

## Verification

- IDA uniqueness checks:
  - Matcher: `0x360570`
  - MOBASkill aim compute: `0x3C5690`
  - Dpad analog: `0xCED010`
  - Dpad update: `0xCECC70`
  - Dpad key: `0xCED320`
  - KMM load: `0x3F9F80`
  - KMM set active: `0x402610`
- Built the native wrapper successfully with MSBuild:
  - Project: `BlueStacksDInputWrapper/BlueStacksDInputWrapper.vcxproj`
  - Configuration: `Release`
  - Platform: `x64`
  - Result: 0 warnings, 0 errors

## Notes

- The existing color matcher prologue signature survived this BlueStacks update.
- The old Dpad RVAs now landed in unrelated code. The new Dpad targets were derived from `ImapRtDpad` RTTI/vtable analysis and confirmed by decompilation behavior.
- `std::string` was not available as an IDA parser type in this database, so the `Kmm_loadPackageCfg` package-name argument was documented as a pointer in IDA while preserving the wrapper's source typedef.
