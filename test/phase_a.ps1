# phase_a.ps1 - Phase A capture-rate test (HANDOFF.md 6.2). PowerShell 5.1.
#
# The script records test/counter.html with FFmpeg directly (not with the app).
# Runs A1 to A5: 10 s each, same region. After each run, frames.py reads the
# counter bar code and writes a JSON report.
#
# All coordinates are PHYSICAL pixels on the virtual screen (gdigrab origin).
# For ddagrab the script converts them to the coordinates of one output
# (monitor), with -OutputIdx, -OutputX and -OutputY.
#
# Example:
#   powershell -ExecutionPolicy Bypass -File test\phase_a.ps1 -X 100 -Y 200 -Width 1200 -Height 700
# Options:
#   -Runs A1,A4      Do only these runs.
#   -Seconds 2       Change the length of each run (smoke test).
#   -Snapshot        Grab one PNG with each backend, then stop (check the region).
#   -Tag full        Add a tag to the file names (for a second set of runs).

param(
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][int]$Width,
    [Parameter(Mandatory = $true)][int]$Height,
    [string[]]$Runs = @('A1', 'A2', 'A3', 'A4', 'A5'),
    [double]$Seconds = 10,
    [int]$OutputIdx = 0,
    [int]$OutputX = 0,
    [int]$OutputY = 0,
    [string]$OutDir = '',
    [string]$Tag = '',
    [switch]$Snapshot
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($OutDir -eq '') { $OutDir = Join-Path $here 'results\phase_a' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# H.264 needs even sizes.
$Width = $Width - ($Width % 2)
$Height = $Height - ($Height % 2)
$size = "{0}x{1}" -f $Width, $Height
$dx = $X - $OutputX
$dy = $Y - $OutputY
$cores = [Environment]::ProcessorCount

# Run table (HANDOFF.md 6.2). x264 settings copy the old app: tune stillimage,
# CRF 27 (Balanced), threads = ceil(fps).
$table = @{
    'A1' = @{ Backend = 'gdigrab'; Encoder = 'libx264 medium';   Fps = 10 }
    'A2' = @{ Backend = 'gdigrab'; Encoder = 'libx264 veryfast'; Fps = 10 }
    'A3' = @{ Backend = 'gdigrab'; Encoder = 'h264_nvenc';       Fps = 10 }
    'A4' = @{ Backend = 'ddagrab'; Encoder = 'h264_nvenc';       Fps = 10 }
    'A5' = @{ Backend = 'ddagrab'; Encoder = 'h264_nvenc';       Fps = 30 }
}

function Get-InputArgs([string]$backend, [double]$fps) {
    if ($backend -eq 'gdigrab') {
        return @('-f', 'gdigrab', '-framerate', "$fps", '-draw_mouse', '0',
                 '-offset_x', "$X", '-offset_y', "$Y", '-video_size', $size, '-i', 'desktop')
    }
    $src = "ddagrab=output_idx={0}:framerate={1}:draw_mouse=0:offset_x={2}:offset_y={3}:video_size={4}" -f $OutputIdx, $fps, $dx, $dy, $size
    return @('-f', 'lavfi', '-i', $src)
}

function Get-EncoderArgs([string]$encoder, [double]$fps) {
    switch ($encoder) {
        'libx264 medium' {
            $t = [Math]::Max(1, [Math]::Min(16, [int][Math]::Ceiling($fps)))
            return @('-c:v', 'libx264', '-preset', 'medium', '-tune', 'stillimage', '-crf', '27', '-pix_fmt', 'yuv420p', '-threads', "$t")
        }
        'libx264 veryfast' {
            $t = [Math]::Max(1, [Math]::Min(16, [int][Math]::Ceiling($fps)))
            return @('-c:v', 'libx264', '-preset', 'veryfast', '-tune', 'stillimage', '-crf', '27', '-pix_fmt', 'yuv420p', '-threads', "$t")
        }
        'h264_nvenc' {
            return @('-c:v', 'h264_nvenc', '-preset', 'p4', '-rc', 'vbr', '-cq', '23', '-b:v', '0')
        }
    }
    throw "Unknown encoder $encoder"
}

# Quote one argument for the Windows command line.
function Join-Args([string[]]$list) {
    ($list | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
}

function Invoke-Ffmpeg([string[]]$ffArgs, [string]$log) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath 'ffmpeg' -ArgumentList (Join-Args $ffArgs) -NoNewWindow -PassThru -RedirectStandardError $log -RedirectStandardOutput "$log.out"
    $null = $p.Handle   # keep the handle, so the exit code and CPU time stay readable
    $p.WaitForExit()
    $sw.Stop()
    $cpu = $p.TotalProcessorTime.TotalSeconds
    return [pscustomobject]@{
        ExitCode = $p.ExitCode
        WallS    = $sw.Elapsed.TotalSeconds
        CpuS     = $cpu
        CpuPct   = [Math]::Round(100.0 * $cpu / $sw.Elapsed.TotalSeconds / $cores, 1)
    }
}

if ($Snapshot) {
    foreach ($b in @('gdigrab', 'ddagrab')) {
        $png = Join-Path $OutDir ("snapshot_{0}.png" -f $b)
        $in = Get-InputArgs $b 10
        $vf = @()
        if ($b -eq 'ddagrab') { $vf = @('-vf', 'hwdownload,format=bgra') }
        $ffArgs = @('-hide_banner', '-y') + $in + $vf + @('-frames:v', '1', '-update', '1', $png)
        $r = Invoke-Ffmpeg $ffArgs (Join-Path $OutDir ("snapshot_{0}.log" -f $b))
        Write-Host ("{0}: exit {1} -> {2}" -f $b, $r.ExitCode, $png)
    }
    return
}

$rows = @()
# With -File, "A3,A4" arrives as one string. Split it.
$Runs = @($Runs | ForEach-Object { $_ -split ',' } | Where-Object { $_ -ne '' })
foreach ($run in $Runs) {
    $cfg = $table[$run]
    if ($null -eq $cfg) { throw "Unknown run $run" }
    $name = $run
    if ($Tag -ne '') { $name = "{0}_{1}" -f $run, $Tag }
    $video = Join-Path $OutDir ("{0}.mp4" -f $name)
    $log = Join-Path $OutDir ("{0}.ffmpeg.log" -f $name)
    $json = Join-Path $OutDir ("{0}.json" -f $name)

    $ffArgs = @('-hide_banner', '-y') + (Get-InputArgs $cfg.Backend $cfg.Fps) + @('-t', "$Seconds") +
              (Get-EncoderArgs $cfg.Encoder $cfg.Fps) + @('-movflags', '+frag_keyframe+empty_moov+default_base_moof', $video)
    Write-Host ("{0}: {1} + {2} at {3} fps, region {4} at ({5},{6})" -f $name, $cfg.Backend, $cfg.Encoder, $cfg.Fps, $size, $X, $Y)
    Set-Content -Path "$log.cmd" -Value ("ffmpeg " + (Join-Args $ffArgs)) -Encoding ASCII
    $r = Invoke-Ffmpeg $ffArgs $log
    if ($r.ExitCode -ne 0) { Write-Warning ("{0}: FFmpeg exit code {1}. See {2}" -f $name, $r.ExitCode, $log) }

    # Last dup= and drop= values in the FFmpeg stats. This FFmpeg build prints
    # them only when they are not 0, so "not found" means 0.
    $text = Get-Content -Raw $log
    $dup = ([regex]::Matches($text, 'dup=(\d+)') | Select-Object -Last 1)
    $drop = ([regex]::Matches($text, 'drop=(\d+)') | Select-Object -Last 1)

    & python (Join-Path $here 'frames.py') $video --counter --expected-fps $cfg.Fps --json $json | Out-Host
    $rep = Get-Content -Raw $json | ConvertFrom-Json
    $c = $rep.counter
    $pageRate = $null
    if ($c.first_value -ne $null -and $rep.duration_s -gt 0) {
        $pageRate = [Math]::Round(($c.last_value - $c.first_value) / $rep.duration_s, 1)
    }
    $rows += [pscustomobject]@{
        Run          = $name
        Backend      = $cfg.Backend
        Encoder      = $cfg.Encoder
        Fps          = $cfg.Fps
        Frames       = $rep.total_frames
        Duration     = $rep.duration_s
        UniqueValues = $c.unique_values
        Expected     = $c.expected_values
        RealRate     = $c.real_capture_rate
        Result       = $c.result
        Failed       = $c.failed_frames
        PageHz       = $pageRate
        PixFmt       = $rep.pix_fmt
        MB           = [Math]::Round((Get-Item $video).Length / 1MB, 3)
        CpuPct       = $r.CpuPct
        FfDup        = $(if ($dup) { [int]$dup.Groups[1].Value } else { 0 })
        FfDrop       = $(if ($drop) { [int]$drop.Groups[1].Value } else { 0 })
    }
}

$rows | Format-Table -AutoSize | Out-Host
$sumName = 'summary'
if ($Tag -ne '') { $sumName = "summary_{0}" -f $Tag }
$rows | ConvertTo-Json | Set-Content -Path (Join-Path $OutDir ("{0}.json" -f $sumName)) -Encoding ASCII
