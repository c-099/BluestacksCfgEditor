# BlueStacks DInput8 Wrapper

This folder contains the native `dinput8.dll` wrapper used by
`BluestacksCfgEditor`.

The wrapper is installed into the BlueStacks application folder as
`dinput8.dll`. At runtime it forwards normal DirectInput calls to the system
`dinput8.dll`, then hooks selected `HD-Player.exe` functions to apply the Brawl
Stars input fixes controlled by the editor.

## Relationship to BluestacksCfgEditor

- The editor writes wrapper settings to
  `<BlueStacks data root>\Engine\UserData\dinput8-config.json`.
- The wrapper reads the same `dinput8-config.json` file.
- When saving live configs, the editor also writes a reload marker beside the
  active packages live config:
  `<BlueStacks data root>\Engine\UserData\InputMapper\UserFiles\<package name>.cfg.reload`.
- The wrapper's hotload thread watches that marker's write time instead of
  watching the config JSON directly. When the marker is created or touched, the
  wrapper reloads the Brawl Stars config from disk; this avoids accidental
  reload loops from BlueStacks writing the config as part of its own apply path.
- The editor bundles a built `dinput8.dll` and can install it into the
  BlueStacks application folder with administrator approval.

## Fixed Color Matcher

BlueStacks uses an internal Imgd color-marker matcher while applying game
control mappings. For Brawl Stars, the wrapper replaces the affected matcher
path with a fixed implementation that scans the runtime vertex/color data and
returns the matching marker indices expected by `HD-Player.exe`.

This fix keeps the working runtime behavior from the original patch, including
the indexed-triangle handling and cursor advance behavior needed to avoid
repeating the same marker.

Credit for the original color-matcher fix, and for the idea to apply it through
a `dinput8.dll` wrapper, goes to Discord user `emilian2470`.

## Build

Build the wrapper from this folder with Visual Studio/MSBuild:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
  ".\BlueStacksDInputWrapper.vcxproj" `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /m
```

The output DLL is:

```text
BlueStacksDInputWrapper\x64\Release\dinput8.dll
```

`BluestacksCfgEditor.csproj` copies that DLL into the editor output and publish
folders when it exists.

## Dependencies

- Windows
- Visual Studio C++ toolchain
- MinHook
