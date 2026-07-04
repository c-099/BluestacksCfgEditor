param(
    [string]$ConfigPath = "D:\BlueStacks_nxt\Engine\UserData\InputMapper\UserFiles\com.supercell.brawlstars.cfg",
    [string]$TextureDumpDir = "D:\BlueStacks_nxt\Logs\image",
    [string]$WrapperConfigPath = "D:\BlueStacks_nxt\Engine\UserData\dinput8-config.json",
    [string]$ProbeLogPath = "D:\BlueStacks_nxt\Engine\UserData\dinput8-image-marker-probe.log",
    [string]$MatchColor = "0x48E03400",
    [int]$DelayMs = 700,
    [switch]$AllSizes,
    [switch]$Yes
)

$ErrorActionPreference = "Stop"

function Touch-ReloadMarker {
    param([string]$CfgPath)
    $reloadPath = "$CfgPath.reload"
    if (-not (Test-Path -LiteralPath $reloadPath)) {
        New-Item -ItemType File -Path $reloadPath -Force | Out-Null
    }
    (Get-Item -LiteralPath $reloadPath).LastWriteTime = Get-Date
}

function Set-FeetTextureCrc {
    param(
        [string]$CfgPath,
        [string]$TextureCrc
    )

    $text = [IO.File]::ReadAllText($CfgPath)
    $pattern = '(?s)("ImageId"\s*:\s*"Feet".*?"TextureCRC"\s*:\s*")0[xX][0-9A-Fa-f]+(")'
    $next = [regex]::Replace(
        $text,
        $pattern,
        { param($match) $match.Groups[1].Value + $TextureCrc + $match.Groups[2].Value })
    if ($next -eq $text) {
        throw "No Feet TextureCRC entries were found in $CfgPath"
    }

    [IO.File]::WriteAllText($CfgPath, $next, [Text.UTF8Encoding]::new($false))
}

function Enable-WrapperProbe {
    param(
        [string]$Path,
        [string]$Color,
        [string]$LogPath
    )

    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $json | Add-Member -NotePropertyName "gImageMarkerProbeEnabled" -NotePropertyValue 1 -Force
    $json | Add-Member -NotePropertyName "gImageMarkerProbeColor" -NotePropertyValue $Color -Force
    $json | Add-Member -NotePropertyName "gImageMarkerProbeLogPath" -NotePropertyValue $LogPath -Force
    $json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding utf8
}

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "Config not found: $ConfigPath"
}

if (-not (Test-Path -LiteralPath $TextureDumpDir)) {
    throw "Texture dump folder not found: $TextureDumpDir"
}

if (-not (Test-Path -LiteralPath $WrapperConfigPath)) {
    throw "Wrapper config not found: $WrapperConfigPath"
}

Write-Host "This probe will modify files while it runs:" -ForegroundColor Yellow
Write-Host "  Config:         $ConfigPath"
Write-Host "  Wrapper config: $WrapperConfigPath"
Write-Host "  Probe log:      $ProbeLogPath"
Write-Host ""
Write-Host "It will create a config backup, enable the wrapper image-marker probe, cycle Feet TextureCRC values, and touch the reload marker."
Write-Host "If no match is found, it restores the config backup. If a match is found, it leaves the config on the matching TextureCRC."
Write-Host ""

if (-not $Yes) {
    $answer = Read-Host "Continue? Type yes to proceed"
    if ($answer -notin @("yes", "y", "YES", "Y")) {
        Write-Host "Aborted. No files were changed."
        exit 2
    }
}

$backupPath = "$ConfigPath.probe-backup.$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Copy-Item -LiteralPath $ConfigPath -Destination $backupPath -Force
Write-Host "Backed up config to $backupPath"

Enable-WrapperProbe -Path $WrapperConfigPath -Color $MatchColor -LogPath $ProbeLogPath
Remove-Item -LiteralPath $ProbeLogPath -Force -ErrorAction SilentlyContinue

$files = Get-ChildItem -LiteralPath $TextureDumpDir -File -Filter "CRC_0x*.png"
if (-not $AllSizes) {
    $files = $files | Where-Object { $_.Name -match "_4096X4096\.png$|_[0-9]{3,4}X[0-9]{3,4}\.png$" }
}

$candidates = $files |
    ForEach-Object {
        if ($_.Name -match "CRC_(0x[0-9A-Fa-f]+)_([0-9]+)X([0-9]+)\.png") {
            [pscustomobject]@{
                Crc = "0x$($Matches[1].Substring(2).ToUpperInvariant())"
                Width = [int]$Matches[2]
                Height = [int]$Matches[3]
                Name = $_.Name
                Length = $_.Length
            }
        }
    } |
    Sort-Object `
        @{ Expression = { $_.Width * $_.Height }; Descending = $true },
        @{ Expression = { $_.Length }; Descending = $true },
        @{ Expression = { $_.Crc }; Descending = $false }

Write-Host "Testing $($candidates.Count) texture CRC candidates for MatchColor $MatchColor"

foreach ($candidate in $candidates) {
    Add-Content -LiteralPath $ProbeLogPath -Value "=== candidate $($candidate.Crc) $($candidate.Width)x$($candidate.Height) $($candidate.Name) ==="
    Set-FeetTextureCrc -CfgPath $ConfigPath -TextureCrc $candidate.Crc
    Touch-ReloadMarker -CfgPath $ConfigPath
    Start-Sleep -Milliseconds $DelayMs

    if (Test-Path -LiteralPath $ProbeLogPath) {
        $matches = Select-String -LiteralPath $ProbeLogPath -Pattern "^MATCH " -SimpleMatch:$false
        if ($matches) {
            Write-Host "MATCH found with TextureCRC $($candidate.Crc) from $($candidate.Name)"
            Write-Host "Config was left on the matching candidate."
            $matches | Select-Object -Last 10 | ForEach-Object { $_.Line }
            exit 0
        }
    }

    Write-Host "No match: $($candidate.Crc) $($candidate.Width)x$($candidate.Height)"
}

Copy-Item -LiteralPath $backupPath -Destination $ConfigPath -Force
Touch-ReloadMarker -CfgPath $ConfigPath
Write-Host "No match found. Restored original config from $backupPath"
exit 1
