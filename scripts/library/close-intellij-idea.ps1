# Requests IntelliJ IDEA windows to close; unsaved work may need attention on the PC.
# run-as: user
# timeout: 30
$ErrorActionPreference = 'Stop'
$session = (Get-Process -Id $PID).SessionId
$processes = @(Get-Process -Name 'idea64', 'idea' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session -and $_.MainWindowHandle -ne 0 })
if ($processes.Count -eq 0) { Write-Output 'No application windows found in this session.'; exit 0 }
$failed = $false
foreach ($process in $processes) {
    try {
        if ($process.CloseMainWindow()) {
            Write-Output "Close requested: $($process.ProcessName). Save prompts may remain on the PC."
        } else {
            Write-Output "Could not request close: $($process.ProcessName). Check the PC, especially if the app is elevated."
            $failed = $true
        }
    } catch {
        Write-Output "Could not close $($process.ProcessName): $($_.Exception.Message)"
        $failed = $true
    }
}
if ($failed) { exit 1 }
exit 0
