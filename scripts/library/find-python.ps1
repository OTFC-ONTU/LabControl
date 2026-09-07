# Lists Python registrations and PATH candidates visible to the student; use a python.exe path in PyCharm settings.
# run-as: user
# timeout: 30
$ErrorActionPreference = 'Stop'
$found = $false
foreach ($root in @('HKCU:\Software\Python', 'HKLM:\Software\Python', 'HKLM:\Software\WOW6432Node\Python')) {
    foreach ($company in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
        foreach ($tag in @(Get-ChildItem -LiteralPath $company.PSPath -ErrorAction SilentlyContinue)) {
            $install = Get-Item -LiteralPath ($tag.PSPath + '\InstallPath') -ErrorAction SilentlyContinue
            if ($install) {
                $path = $install.GetValue('ExecutablePath')
                if (-not $path -and $install.GetValue('')) { $path = Join-Path $install.GetValue('') 'python.exe' }
                if ($path) {
                    $visible = Test-Path -LiteralPath $path -PathType Leaf
                    Write-Output "$($tag.PSChildName): $path (visible to this user: $visible)"
                    $found = $true
                }
            }
        }
    }
}
foreach ($command in @(Get-Command python.exe, python3.exe, py.exe -CommandType Application -ErrorAction SilentlyContinue)) {
    Write-Output "PATH candidate: $($command.Source) (may be a Windows Store alias)"
    $found = $true
}
if (-not $found) { Write-Output 'No Python registration or PATH candidate visible. Check the Python installation for all users.' }
Write-Output 'In PyCharm, select an existing interpreter using its full python.exe path. This script does not execute Python or change settings.'
exit 0
