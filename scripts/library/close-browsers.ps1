# Closes every browser window in the student's session (Edge, Chrome, Firefox, Opera).
# run-as: user
# timeout: 30
$names = 'msedge', 'chrome', 'firefox', 'opera', 'brave'
$closed = 0
foreach ($name in $names) {
    $processes = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($processes) {
        $processes | Stop-Process -Force -ErrorAction SilentlyContinue
        $closed += $processes.Count
        Write-Output "closed $name ($($processes.Count) process(es))"
    }
}
Write-Output "done: $closed process(es) closed"
exit 0
