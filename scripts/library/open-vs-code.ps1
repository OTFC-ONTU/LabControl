# Opens Visual Studio Code on the student desktop.
# run-as: user
# timeout: 120
$ErrorActionPreference = 'Stop'
# Set this only for a portable/custom installation not found below.
$overridePath = ''
$executables = @('Code.exe')
$patterns = @('Microsoft VS Code\Code.exe', 'Programs\Microsoft VS Code\Code.exe')
$candidates = @()
if ($overridePath) {
    if (-not (Test-Path -LiteralPath $overridePath -PathType Leaf)) { throw "Executable not found: $overridePath" }
    $candidates += $overridePath
} else {
    foreach ($exe in $executables) {
        foreach ($hive in @('HKCU:', 'HKLM:')) {
            foreach ($key in @('SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths', 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths')) {
                $entry = Get-Item -LiteralPath "$hive\$key\$exe" -ErrorAction SilentlyContinue
                if ($entry -and $entry.GetValue('')) { $candidates += $entry.GetValue('').Trim('"') }
            }
        }
        $command = Get-Command $exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $candidates += $command.Source }
    }
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA) | Where-Object { $_ } | Select-Object -Unique
    foreach ($root in $roots) {
        foreach ($pattern in $patterns) {
            $candidates += Get-ChildItem -Path (Join-Path $root $pattern) -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -ExpandProperty FullName
        }
    }
}
$path = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
if (-not $path) { throw 'Visual Studio Code was not found. Install it or set $overridePath to its executable.' }
Start-Process -FilePath $path -WorkingDirectory (Split-Path -Parent $path) -Verb Open -ErrorAction Stop
Write-Output "Launch requested: $path"
exit 0
