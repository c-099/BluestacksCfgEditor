param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$EditorProject = Join-Path $RepoRoot "BluestacksCfgEditor.csproj"
$WrapperProject = Join-Path $RepoRoot "BlueStacksDInputWrapper\BlueStacksDInputWrapper.vcxproj"
$ArtifactsRoot = Join-Path $RepoRoot "artifacts\release"
$StageRoot = Join-Path $ArtifactsRoot "stage"

function Resolve-MSBuild {
    $command = Get-Command "msbuild.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "MSBuild.exe was not found. Install Visual Studio C++ build tools or add MSBuild to PATH."
}

function Get-ProjectVersion {
    if ($Version) {
        return $Version
    }

    [xml]$projectXml = Get-Content -LiteralPath $EditorProject
    $projectVersion = $projectXml.Project.PropertyGroup.Version |
        Where-Object { $_ } |
        Select-Object -First 1

    if ($projectVersion) {
        return $projectVersion
    }

    return "0.0.0"
}

function Resolve-BlueStacksDataRoot {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        try {
            $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
            try {
                $subKey = $baseKey.OpenSubKey("SOFTWARE\BlueStacks_nxt")
                if ($subKey) {
                    try {
                        $value = [string]$subKey.GetValue("UserDefinedDir", "")
                        if (-not [string]::IsNullOrWhiteSpace($value)) {
                            return $value
                        }
                    } finally {
                        $subKey.Close()
                    }
                }
            } finally {
                $baseKey.Close()
            }
        } catch {
            # Ignore registry view failures and fall back.
        }
    }

    return "C:\ProgramData\BlueStacks_nxt"
}

function Copy-CustomCursorAssets {
    param(
        [string]$DestinationRoot
    )

    $configPath = Join-Path (Join-Path (Resolve-BlueStacksDataRoot) "Engine\UserData") "dinput8-config.json"
    if (-not (Test-Path -LiteralPath $configPath)) {
        Write-Host "No wrapper config found for cursor packaging: $configPath"
        return
    }

    $settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $cursorKeys = @(
        "gCustomCursorMousePath",
        "gCustomCursorMobaPath",
        "gCustomCursorMobaRightPath",
        "gCustomCursorBlankPath"
    )

    $cursorDir = Join-Path $DestinationRoot "custom-cursors"
    $manifest = @()

    foreach ($key in $cursorKeys) {
        $property = $settings.PSObject.Properties[$key]
        if (-not $property) {
            continue
        }

        $sourcePath = [string]$property.Value
        if ([string]::IsNullOrWhiteSpace($sourcePath)) {
            continue
        }

        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            Write-Host "Skipping missing cursor asset for $key`: $sourcePath"
            continue
        }

        New-Item -ItemType Directory -Force -Path $cursorDir | Out-Null
        $fileName = Split-Path -Leaf $sourcePath
        $destinationPath = Join-Path $cursorDir $fileName
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force

        $manifest += [pscustomobject]@{
            setting = $key
            source = $sourcePath
            packagedAs = "custom-cursors/$fileName"
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash
        }
    }

    if ($manifest.Count -gt 0) {
        $manifestPath = Join-Path $cursorDir "cursor-manifest.json"
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        Write-Host "Packaged $($manifest.Count) custom cursor asset(s)."
    }
}

$ResolvedVersion = Get-ProjectVersion
$PackageName = "BluestacksCfgEditor-v$ResolvedVersion-$Runtime"
$StageDir = Join-Path $StageRoot $PackageName
$PublishDir = Join-Path $StageDir "app"
$BuildOutputDir = Join-Path $StageRoot "build\$PackageName\bin\"
$ZipPath = Join-Path $ArtifactsRoot "$PackageName.zip"

if (Test-Path -LiteralPath $StageDir) {
    Remove-Item -LiteralPath $StageDir -Recurse -Force
}
if (Test-Path -LiteralPath $BuildOutputDir) {
    Remove-Item -LiteralPath $BuildOutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $ArtifactsRoot | Out-Null

$MSBuild = Resolve-MSBuild

Write-Host "Building wrapper..."
& $MSBuild $WrapperProject /p:Configuration=$Configuration /p:Platform=x64 /m
if ($LASTEXITCODE -ne 0) {
    throw "Wrapper build failed with exit code $LASTEXITCODE."
}

Write-Host "Publishing editor..."
dotnet publish $EditorProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $PublishDir `
    /p:PublishSingleFile=true `
    /p:OutputPath="$BuildOutputDir"
if ($LASTEXITCODE -ne 0) {
    throw "Editor publish failed with exit code $LASTEXITCODE."
}

Get-ChildItem -LiteralPath $PublishDir -Filter "*.pdb" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

Copy-Item -LiteralPath (Join-Path $RepoRoot "README.md") -Destination (Join-Path $StageDir "README.md") -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "LICENSE") -Destination (Join-Path $StageDir "LICENSE") -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "BlueStacksDInputWrapper\README.md") -Destination (Join-Path $StageDir "WRAPPER-README.md") -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "install-wrapper.bat") -Destination (Join-Path $PublishDir "install-wrapper.bat") -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "uninstall-wrapper.bat") -Destination (Join-Path $PublishDir "uninstall-wrapper.bat") -Force

$BrawlStarsConfig = Join-Path $RepoRoot "publish\com.supercell.brawlstars.cfg"
if (Test-Path -LiteralPath $BrawlStarsConfig) {
    Copy-Item -LiteralPath $BrawlStarsConfig -Destination (Join-Path $StageDir "com.supercell.brawlstars.cfg") -Force
}

Copy-CustomCursorAssets -DestinationRoot $StageDir

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

Write-Host "Creating zip..."
Compress-Archive -Path (Join-Path $StageDir "*") -DestinationPath $ZipPath -CompressionLevel Optimal

Write-Host "Release package created:"
Write-Host $ZipPath
