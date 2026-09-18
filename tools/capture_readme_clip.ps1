# Regenerates docs/media/instancing-toggle.gif from the shipped player.
#
# The clip shows GPU instancing being switched on at 10,000 agents with spatialHash and zeroAlloc
# already active: the draw-call counter collapses, the technique row flips to ON, and the picture
# barely changes. The toggle does not respawn, so the field is identical either side of the cut,
# which is what makes the point land.
#
#   pwsh -File tools/capture_readme_clip.ps1
#
# The player writes a numbered PNG sequence and this assembles it. The sequence is an intermediate
# and is deleted afterwards; only the GIF is committed.

[CmdletBinding()]
param(
    [int]    $Agents       = 10000,
    [int]    $SettleFrames = 240,
    [int]    $ClipFrames   = 60,
    [int]    $ToggleAt     = 20,
    # Every 18th rendered frame: a 10 fps clip at close to real-time speed, leaving ~94% of frames
    # free of a readback. See PresentationCapture for why that matters to the numbers on screen.
    [int]    $Interval     = 18,
    [int]    $Fps          = 10,
    [int]    $Width        = 1280,
    [int]    $Height       = 720,
    [double] $MaxMB        = 8.0,
    [string] $Output       = 'docs/media/instancing-toggle.gif'
)

$ErrorActionPreference = 'Stop'

$repo   = Split-Path -Parent $PSScriptRoot
$player = Join-Path $repo 'Builds/Windows64/FrameBudget.exe'
$gif    = Join-Path $repo $Output
$frames = Join-Path ([System.IO.Path]::GetTempPath()) ("framebudget-clip-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$log    = Join-Path ([System.IO.Path]::GetTempPath()) 'framebudget-clip.log'

if (-not (Test-Path $player)) {
    Write-Error @"
No player at $player

Build one first:
  & "C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Unity.exe" -batchmode -quit ``
      -projectPath "$repo" -executeMethod FrameBudget.EditorTools.BuildBenchmark.Build
"@
    exit 1
}

New-Item -ItemType Directory -Force -Path $frames | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $gif) | Out-Null

Write-Host "Recording $ClipFrames frames at $Agents agents, toggling gpuInstancing at frame $ToggleAt..."

& $player `
    -screen-fullscreen 0 -screen-width $Width -screen-height $Height `
    -frameBudgetClip $frames `
    -frameBudgetAgents $Agents `
    -frameBudgetSettleFrames $SettleFrames `
    -frameBudgetClipFrames $ClipFrames `
    -frameBudgetClipToggleAt $ToggleAt `
    -frameBudgetClipInterval $Interval `
    -logFile $log | Out-Null

$png = Get-ChildItem -Path $frames -Filter 'frame_*.png' -ErrorAction SilentlyContinue
if ($png.Count -lt $ClipFrames) {
    Write-Host ''
    Write-Host "Expected $ClipFrames frames, found $($png.Count). The player's log:" -ForegroundColor Red
    if (Test-Path $log) { Get-Content $log -Tail 40 }
    Write-Error 'Clip capture incomplete'
    exit 1
}
Write-Host "  $($png.Count) frames captured"

Select-String -Path $log -Pattern '\[FrameBudget\] Clip (frame|:)' | ForEach-Object { Write-Host "  $($_.Line.Trim())" }

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) {
    Write-Host ''
    Write-Warning 'ffmpeg is not on PATH, so the GIF was not assembled.'
    Write-Host ''
    Write-Host 'Install it with:'
    Write-Host '  winget install ffmpeg'
    Write-Host '(a new shell picks up the PATH change), then run this script again.'
    Write-Host ''
    Write-Host 'Or assemble the sequence by hand - ScreenToGif imports it directly'
    Write-Host 'as an image sequence (Insert > Media, select all frames) and exports a GIF:'
    Write-Host "  $frames" -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'The folder is left in place for that. Delete it once you are done.'
    exit 1
}

# Two passes. palettegen builds a palette from the actual frames and paletteuse maps to it, which
# is worth the extra pass here: a single-pass GIF quantizes to a generic 256-color palette and puts
# visible banding across the agent field and dither noise through the panel text.
function Build-Gif([int]$rate, [int]$scaleWidth) {
    $palette = Join-Path $frames 'palette.png'
    $scale = if ($scaleWidth -gt 0) { "scale=${scaleWidth}:-1:flags=lanczos," } else { '' }
    & $ffmpeg -y -loglevel error -framerate $rate -i (Join-Path $frames 'frame_%04d.png') `
        -vf "${scale}palettegen=stats_mode=diff" $palette
    & $ffmpeg -y -loglevel error -framerate $rate -i (Join-Path $frames 'frame_%04d.png') -i $palette `
        -lavfi "${scale}paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" $gif
    Remove-Item $palette -Force -ErrorAction SilentlyContinue
    return (Get-Item $gif).Length / 1MB
}

$sizeMB = Build-Gif -rate $Fps -scaleWidth 0
$note = "$Fps fps, full $Width px"

# Under the size limit in two documented steps rather than one opaque re-encode.
if ($sizeMB -gt $MaxMB) {
    Write-Host ("  {0:N1} MB at $Fps fps is over the {1:N0} MB limit; retrying at 8 fps..." -f $sizeMB, $MaxMB)
    $sizeMB = Build-Gif -rate 8 -scaleWidth 0
    $note = "8 fps, full $Width px"
}
if ($sizeMB -gt $MaxMB) {
    Write-Host ("  {0:N1} MB still over; retrying at 8 fps scaled to 960 px..." -f $sizeMB)
    $sizeMB = Build-Gif -rate 8 -scaleWidth 960
    $note = '8 fps, scaled to 960 px'
}

Remove-Item -Recurse -Force $frames

Write-Host ''
if ($sizeMB -gt $MaxMB) {
    Write-Warning ("{0} is {1:N1} MB, still over the {2:N0} MB target ($note)." -f $gif, $sizeMB, $MaxMB)
} else {
    Write-Host ("Wrote {0} ({1:N1} MB, $note)" -f $gif, $sizeMB) -ForegroundColor Green
}
Write-Host 'Check the draw-call counter and the technique row across the cut before committing.'
