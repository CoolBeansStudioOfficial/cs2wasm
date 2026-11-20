# Runs a build command and reports its wall time and the peak memory of every process it started (summed over all of
# them at each sample: compilers, servers, linkers), for comparing compilers' cost to build the same program.
#   powershell -File tools/measure-build.ps1 -Label <name> -Command "<command line>" [-Dir <working dir>]
# Samples every 500 ms: a peak shorter than that can be missed. Processes that were running before the build (another
# build, an editor) are not counted; a build server the build starts is.
param([string]$Label, [string]$Command, [string]$Dir = (Get-Location).Path)
$before = @{}
Get-Process | ForEach-Object { $before[$_.Id] = $true }
$t0 = Get-Date
$p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", $Command -WorkingDirectory $Dir -NoNewWindow -PassThru
$peakWs = 0; $peakPriv = 0; $byName = @{}
while (-not $p.HasExited) {
    $ws = 0; $priv = 0
    foreach ($q in Get-Process) {
        if ($before.ContainsKey($q.Id)) { continue }
        try { $ws += $q.WorkingSet64; $priv += $q.PrivateMemorySize64 } catch { continue }
        $n = $q.ProcessName
        if (-not $byName.ContainsKey($n) -or $byName[$n] -lt $q.WorkingSet64) { $byName[$n] = $q.WorkingSet64 }
    }
    if ($ws -gt $peakWs) { $peakWs = $ws }
    if ($priv -gt $peakPriv) { $peakPriv = $priv }
    Start-Sleep -Milliseconds 500
}
$secs = ((Get-Date) - $t0).TotalSeconds
"{0}: exit {1}, {2:N0} s, peak working set {3:N2} GB (private {4:N2} GB)" -f $Label, $p.ExitCode, $secs, ($peakWs / 1GB), ($peakPriv / 1GB)
$byName.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 5 | ForEach-Object { "    {0,-20} {1:N2} GB" -f $_.Key, ($_.Value / 1GB) }
