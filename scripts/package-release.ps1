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

$ResolvedVersion = Get-ProjectVersion
$PackageName = "BluestacksCfgEditor-v$ResolvedVersion-$Runtime"
$StageDir = Join-Path $StageRoot $PackageName
$PublishDir = Join-Path $StageDir "app"
$ZipPath = Join-Path $ArtifactsRoot "$PackageName.zip"

if (Test-Path -LiteralPath $StageDir) {
    Remove-Item -LiteralPath $StageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $ArtifactsRoot | Out-Null

$MSBuild = Resolve-MSBuild

Write-Host "Building wrapper..."
& $MSBuild $WrapperProject /p:Configuration=$Configuration /p:Platform=x64 /m

Write-Host "Publishing editor..."
dotnet publish $EditorProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $PublishDir `
    /p:PublishSingleFile=true

Get-ChildItem -LiteralPath $PublishDir -Filter "*.pdb" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

Copy-Item -LiteralPath (Join-Path $RepoRoot "README.md") -Destination (Join-Path $StageDir "README.md") -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "LICENSE") -Destination (Join-Path $StageDir "LICENSE") -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "BlueStacksDInputWrapper\README.md") -Destination (Join-Path $StageDir "WRAPPER-README.md") -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "install-wrapper.bat") -Destination (Join-Path $StageDir "install-wrapper.bat") -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot "uninstall-wrapper.bat") -Destination (Join-Path $StageDir "uninstall-wrapper.bat") -Force

$BrawlStarsConfig = Join-Path $RepoRoot "publish\com.supercell.brawlstars.cfg"
if (Test-Path -LiteralPath $BrawlStarsConfig) {
    Copy-Item -LiteralPath $BrawlStarsConfig -Destination (Join-Path $StageDir "com.supercell.brawlstars.cfg") -Force
}

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

Write-Host "Creating zip..."
Compress-Archive -Path (Join-Path $StageDir "*") -DestinationPath $ZipPath -CompressionLevel Optimal

Write-Host "Release package created:"
Write-Host $ZipPath
