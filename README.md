# BlueStacks CFG Editor

A Windows desktop editor for BlueStacks 5 `.cfg` game-control mapping files.

> WARNING!! 
> Editing a live BlueStacks configuration can break a control scheme. Keep a known-good copy of important configurations.

<img src="./img/screenshot.png" alt="drawing" width="500"/>

## Features

- Opens BlueStacks `.cfg` files and JSON files
- Discovers installed package configurations from the BlueStacks data directory
- Edits common control properties and fields for D-pad, MOBA skill, tap-repeat, and script controls
- Provides an interactive 16:9 position preview
- Supports direct JSON editing for individual controls
- Edits wrapper settings in a separate `dinput8-config.json` file under the BlueStacks user data folder
- Saves to a separate file or directly to the live BlueStacks configuration
- Keeps one rolling `.bak` backup before overwriting an existing live configuration
- Bundles the BlueStacks `dinput8.dll` wrapper and can install it with administrator approval

## Requirements

- Windows 10 or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build from source
- BlueStacks 5 for live configuration discovery and saving

## Build and run

Clone the repository, then run:

```powershell
dotnet run --project BluestacksCfgEditor.csproj
```

To create a release build:

```powershell
dotnet publish BluestacksCfgEditor.slnx --configuration Release
```

The release output is written to `bin/Release/net10.0-windows/win-x64/publish/` and produces a self-contained single-file executable.

To build the native wrapper, publish the editor, and create a GitHub release zip:

```powershell
.\scripts\package-release.ps1
```

The release zip is written to:

```text
artifacts\release\BluestacksCfgEditor-v<version>-win-x64.zip
```

## Usage

Place the release files together before running the editor:

```text
<BlueStacks data directory>\Engine\UserData\BluestacksCfgEditor.exe
<BlueStacks data directory>\Engine\UserData\dinput8.dll
```

If you start `BluestacksCfgEditor.exe` from another folder, it will offer to
copy itself and the bundled `dinput8.dll` into the BlueStacks user data folder
and relaunch from there.

The wrapper DLL must also be installed in the BlueStacks application folder for
live functionality:

```text
<BlueStacks install directory>\dinput8.dll
```

The editor prompts to copy it there with administrator approval when it is
missing.

1. Select **Open Config** to edit an existing `.cfg` or `.json` file, or select **Open Live** to load the configuration for the package shown in the package field.
2. Choose a control scheme and control from the left panel.
3. Edit supported properties, or use **Advanced JSON** for fields not exposed by the form.
4. Select **Save As** to write a separate file.
5. Select **Save To Live** only when you are ready to replace the package's active configuration.

BlueStacks is discovered from the `HKLM\SOFTWARE\BlueStacks_nxt` registry key. If that key is unavailable, the editor falls back to:

```text
C:\ProgramData\BlueStacks_nxt
```

Live configuration files are read from and written to:

```text
<BlueStacks data directory>\Engine\UserData\InputMapper\UserFiles\<package>.cfg
```

Wrapper settings are saved separately beside the installed wrapper DLL:

```text
<BlueStacks data directory>\Engine\UserData\dinput8-config.json
```

When replacing an existing live file, the editor creates or overwrites one backup beside it:

```text
<package>.cfg.bak
```

Wrapper settings use the same rolling backup pattern:

```text
dinput8-config.json.bak
```

## Wrapper

The native BlueStacks wrapper source is included in:

```text
BlueStacksDInputWrapper\
```

Build that project first when you need a fresh `dinput8.dll`; the editor build copies
`BlueStacksDInputWrapper\x64\Release\dinput8.dll` into its output and publish folders
when the DLL exists.

## Limitations

- The editor currently exposes dedicated fields for a subset of BlueStacks control types. Other controls remain editable through the Advanced JSON tab.
- BlueStacks configuration formats are not officially documented and may change between releases.
- This project is not affiliated with or endorsed by BlueStacks.
##
To support my projects:
<br>
<a href="https://www.buymeacoffee.com/ninenines" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-red.png" alt="Buy Me a Coffee" style="height: 40px !important;width: 145px !important;" ></a>
<br>
Join my discord for help, questions or suggestions: [discord.gg/mDx55QKT3s](https://discord.gg/mDx55QKT3s)
