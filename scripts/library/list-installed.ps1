# Lists the installed programs with their versions, one per line, sorted by name.
# timeout: 120
$keys = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
Get-ItemProperty $keys -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -and -not $_.SystemComponent } |
    Sort-Object DisplayName -Unique |
    ForEach-Object { Write-Output ("{0}  {1}" -f $_.DisplayName, $_.DisplayVersion) }
exit 0
