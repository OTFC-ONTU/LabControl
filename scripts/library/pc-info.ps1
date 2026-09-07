# Prints the PC's name, addresses, uptime, free disk space and the logged-on user.
# timeout: 60
$os = Get-CimInstance Win32_OperatingSystem
$uptime = (Get-Date) - $os.LastBootUpTime
Write-Output "computer: $env:COMPUTERNAME"
Write-Output "windows: $($os.Caption) $($os.Version)"
Write-Output ("uptime: {0}d {1:00}:{2:00}" -f $uptime.Days, $uptime.Hours, $uptime.Minutes)
foreach ($ip in (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' })) {
    Write-Output "ip: $($ip.IPAddress) ($($ip.InterfaceAlias))"
}
foreach ($disk in (Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3")) {
    Write-Output ("disk {0} free {1:N1} GB of {2:N1} GB" -f $disk.DeviceID, ($disk.FreeSpace / 1GB), ($disk.Size / 1GB))
}
$user = (Get-CimInstance Win32_ComputerSystem).UserName
if ($user) { Write-Output "logged on: $user" } else { Write-Output "logged on: nobody" }
exit 0
