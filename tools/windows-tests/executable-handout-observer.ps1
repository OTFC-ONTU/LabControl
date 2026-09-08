# Isolated Windows VM fixture only. Run as SYSTEM; caller coordinates actual send_file.
param([int]$TimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
$ExpectedInstallation = 'b2e98b07-9256-4823-9631-f350c46b014e'
$ObservedName = 'M4-no-run-agent.exe'
if ($TimeoutSeconds -lt 30 -or $TimeoutSeconds -gt 600) { throw 'Timeout must be 30..600 seconds.' }
if (![Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { throw 'SYSTEM observer required.' }
function Assert-Trusted([string]$Path, [bool]$Private) {
    $item = Get-Item -LiteralPath $Path -Force
    $current = $item
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point refused.' }
        if ($current -is [IO.FileInfo]) { $current = $current.Directory } else { $current = $current.Parent }
    }
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18','S-1-5-32-544')) { throw 'Untrusted fixture owner.' }
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow') { throw 'Unexpected access rule.' }
        if ($rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')) {
            if ($Private -or (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership') -ne 0)) { throw 'Untrusted fixture access.' }
        }
    }
}
function Write-Result([string]$Name, $Value) {
    $target = Join-Path $Root $Name
    [IO.File]::WriteAllText(($target + '.tmp'), ($Value | ConvertTo-Json -Depth 4))
    [IO.File]::Move(($target + '.tmp'), $target)
}
$statePath = 'C:\ProgramData\LabControl\installation.json'
Assert-Trusted $statePath $true
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if ($state.installation_id -ne $ExpectedInstallation -or $state.removal_ready) { throw 'Unexpected installation.' }
$install = 'C:\Program Files\LabControl'
Assert-Trusted $install $false
$marker = Join-Path $install 'installation-id'
Assert-Trusted $marker $false
if ([IO.File]::ReadAllText($marker).Trim() -ne $ExpectedInstallation) { throw 'Unexpected installation marker.' }
$currentPath = Join-Path $install 'app\current'
Assert-Trusted $currentPath $false
$version = [IO.File]::ReadAllText($currentPath).Trim()
if ($version -notmatch '^\d+\.\d+(?:\.\d+){0,2}(?:\+[a-zA-Z0-9.-]+)?$') { throw 'Invalid installed version path.' }
$source = Join-Path $install ('app\' + $version + '\agent.exe')
Assert-Trusted $source $false
$Root = Join-Path $env:ProgramData ('LabControl.ExecutableHandoutProbe.' + [guid]::NewGuid().ToString('N'))
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
$acl.SetAccessRuleProtection($true,$false)
foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
[void][IO.Directory]::CreateDirectory($Root, $acl)
$control = Join-Path $Root $ObservedName
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
[IO.File]::Copy($source,$control,$false)
if ((Get-FileHash -LiteralPath $control -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Control copy differs.' }
$subscription = 'LabControl.ExecutableHandoutProbe.' + [guid]::NewGuid().ToString('N')
$positive = $false
$launches = 0
$deliverySeen = $false
try {
    Register-WmiEvent -Query "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName='$ObservedName'" -SourceIdentifier $subscription | Out-Null
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$control; $start.Arguments='--version'; $start.WorkingDirectory=$Root
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    $controlPid=$process.Id
    $controlOutput=$process.StandardOutput.ReadToEndAsync()
    $controlError=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(30000)){$process.Kill();$process.WaitForExit();throw 'Version-only control timed out'}
    [IO.File]::WriteAllText((Join-Path $Root 'version.txt'),$controlOutput.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $Root 'control-error.txt'),$controlError.GetAwaiter().GetResult())
    $controlExit=$process.ExitCode
    $process.Dispose()
    if($controlExit -ne 0){throw 'Version-only positive control failed'}
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (!$positive -and [DateTime]::UtcNow -lt $limit) {
        foreach ($event in @(Get-Event -SourceIdentifier $subscription -ErrorAction SilentlyContinue)) {
            if ([uint32]$event.SourceEventArgs.NewEvent.ProcessID -eq $controlPid) { $positive = $true }
            Remove-Event -EventIdentifier $event.EventIdentifier
        }
        if (!$positive) { Start-Sleep -Milliseconds 100 }
    }
    if (!$positive) { throw 'Observer missed its positive control.' }
    # Drop only this subscription's positive-control events before the measured window.
    Get-Event -SourceIdentifier $subscription -ErrorAction SilentlyContinue | Remove-Event
    Write-Result 'ready.json' @{ ready=$true; positive_control=$true; observed_name=$ObservedName; timeout_seconds=$TimeoutSeconds; source_sha256=$sourceHash.ToLowerInvariant() }
    Write-Output ('READY ' + $Root)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $finishAfter = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($event in @(Get-Event -SourceIdentifier $subscription -ErrorAction SilentlyContinue)) {
            $launches++
            Remove-Event -EventIdentifier $event.EventIdentifier
        }
        $done = Join-Path $Root 'delivery-complete.json'
        if (!$deliverySeen -and (Test-Path -LiteralPath $done)) {
            Assert-Trusted $done $true
            $delivery = Get-Content -LiteralPath $done -Raw | ConvertFrom-Json
            if ($delivery.ok -ne $true -or !$delivery.job_id) { throw 'Delivery handshake did not confirm a successful real job.' }
            $deliverySeen = $true
            $finishAfter = [DateTime]::UtcNow.AddSeconds(10)
        }
        if ($deliverySeen -and [DateTime]::UtcNow -ge $finishAfter) { break }
        Start-Sleep -Milliseconds 100
    }
    foreach ($event in @(Get-Event -SourceIdentifier $subscription -ErrorAction SilentlyContinue)) { $launches++; Remove-Event -EventIdentifier $event.EventIdentifier }
    Write-Result 'result.json' @{ ok=($positive -and $deliverySeen -and [DateTime]::UtcNow -ge $finishAfter -and $launches -eq 0); positive_control=$positive; delivery_confirmed=$deliverySeen; matching_launches=$launches; observed_name=$ObservedName }
} catch {
    Write-Result 'result.json' @{ ok=$false; positive_control=$positive; delivery_confirmed=$deliverySeen; matching_launches=$launches; error=$_.Exception.GetType().Name; detail=$_.Exception.Message }
} finally {
    Unregister-Event -SourceIdentifier $subscription -ErrorAction SilentlyContinue
    Get-Event -SourceIdentifier $subscription -ErrorAction SilentlyContinue | Remove-Event
}
Get-Content -LiteralPath (Join-Path $Root 'result.json') -Raw
