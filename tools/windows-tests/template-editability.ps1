# Test fixture only. SYSTEM schedules the currently logged-in recorded student with a limited token.
param([switch]$Desktop, [string]$Root)
$ErrorActionPreference = 'Stop'
$ExpectedInstallation = 'b2e98b07-9256-4823-9631-f350c46b014e'
$ExpectedHashes = @{
    'Desktop/M4-template.txt' = 'd71d51a053a49b8a853d495bd3497884246aca01ec23bcbfc558dfff44510bca'
    'Documents/M4-fixture/M4-template.txt' = '84eb0da2661154ba4bbacaddec4395f7623a4b4472a8882772a429fa6bcbe57e'
}
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class TemplateNative {
 [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
 [DllImport("wtsapi32.dll", SetLastError=true)] public static extern bool WTSQueryUserToken(uint session, out IntPtr token);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr token);
 [DllImport("userenv.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool GetDefaultUserProfileDirectory(StringBuilder path, ref uint size);
}
'@
function Assert-NoLinks([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point refused.' }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
if (!$Desktop) {
    if (![Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { throw 'SYSTEM launcher required.' }
    $statePath = 'C:\ProgramData\LabControl\installation.json'
    Assert-NoLinks $statePath
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.installation_id -ne $ExpectedInstallation -or !$state.create_student_account -or !$state.created_student_sid -or $state.removal_ready) { throw 'Unexpected installation.' }
    $sid = [string]$state.created_student_sid
    $token = [IntPtr]::Zero
    if (![TemplateNative]::WTSQueryUserToken([TemplateNative]::WTSGetActiveConsoleSessionId(), [ref]$token)) { throw 'No active student token.' }
    try {
        $active = New-Object Security.Principal.WindowsIdentity($token)
        try { if ($active.User.Value -ne $sid -or $active.Groups.Value -contains 'S-1-5-32-544') { throw 'Active user is not the recorded standard student.' } }
        finally { $active.Dispose() }
    } finally { [void][TemplateNative]::CloseHandle($token) }
    $Root = Join-Path $env:ProgramData ('LabControl.TemplateProbe.' + [guid]::NewGuid().ToString('N'))
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($who in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($who)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    [void][IO.Directory]::CreateDirectory($Root, $acl)
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $Root 'probe.ps1')
    [IO.File]::WriteAllText((Join-Path $Root 'student-sid.txt'), $sid)
    $output = Join-Path $Root 'output'
    [void][IO.Directory]::CreateDirectory($output)
    $outputAcl = Get-Acl -LiteralPath $output
    $outputAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $output -AclObject $outputAcl
    $scheduler = New-Object -ComObject Schedule.Service
    $scheduler.Connect()
    $folder = $scheduler.GetFolder('\')
    $definition = $scheduler.NewTask(0)
    $definition.Principal.UserId = $sid
    $definition.Principal.LogonType = 3
    $definition.Principal.RunLevel = 0
    $definition.Settings.ExecutionTimeLimit = 'PT2M'
    $definition.Settings.DisallowStartIfOnBatteries = $false
    $definition.Settings.StopIfGoingOnBatteries = $false
    $action = $definition.Actions.Create(0)
    $action.Path = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $Root 'probe.ps1') + '" -Desktop -Root "' + $Root + '"'
    $name = 'LabControl.TemplateProbe.' + [guid]::NewGuid().ToString('N')
    $task = $folder.RegisterTaskDefinition($name, $definition, 2, $sid, $null, 3, $null)
    $xml = $task.Xml
    try {
        [void]$task.Run($null)
        $result = Join-Path $output 'result.json'
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while (!(Test-Path -LiteralPath $result) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
        Write-Output ('Fixture output: ' + $Root)
        if (!(Test-Path -LiteralPath $result)) { throw 'Probe did not return.' }
        Get-Content -LiteralPath $result -Raw
    } finally { if ($folder.GetTask($name).Xml -eq $xml) { $folder.DeleteTask($name, 0) } }
    return
}
$checks = @()
try {
    Assert-NoLinks $Root
    $sid = [IO.File]::ReadAllText((Join-Path $Root 'student-sid.txt'))
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($identity.User.Value -ne $sid -or $identity.IsSystem -or $identity.Groups.Value -contains 'S-1-5-32-544') { throw 'Expected standard student token, including no deny-only Administrators SID.' }
    $checks += @{ name='standard-student-token'; ok=$true }
    [uint32]$size = 32768
    $buffer = New-Object Text.StringBuilder 32768
    if (![TemplateNative]::GetDefaultUserProfileDirectory($buffer, [ref]$size)) { throw 'Default profile unavailable.' }
    $default = $buffer.ToString()
    foreach ($relative in $ExpectedHashes.Keys) {
        $leaf = if ($relative.StartsWith('Desktop/')) { Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'M4-template.txt' } else { Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'M4-fixture\M4-template.txt' }
        $source = Join-Path $default $relative.Replace('/','\')
        Assert-NoLinks $leaf; Assert-NoLinks $source
        if ((Hash $leaf) -ne $ExpectedHashes[$relative] -or (Hash $source) -ne $ExpectedHashes[$relative]) { throw 'Unexpected fixture bytes.' }
        # Exclusive handle prevents another writer racing the reversible append.
        $handle = [IO.File]::Open($leaf, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $originalLength = $handle.Length
        try {
            $marker = [Text.Encoding]::ASCII.GetBytes(' M4 reversible editability probe')
            [void]$handle.Seek(0, [IO.SeekOrigin]::End)
            $handle.Write($marker, 0, $marker.Length)
            $handle.Flush($true)
            if ($handle.Length -ne ($originalLength + $marker.Length)) { throw 'Append verification failed.' }
        } finally {
            $handle.SetLength($originalLength)
            $handle.Flush($true)
            $handle.Dispose()
        }
        if ((Hash $leaf) -ne $ExpectedHashes[$relative]) { throw 'Original fixture restoration failed.' }
        $checks += @{ name=($relative + ':actual-file-write-access'); ok=$true }
        $temporary = Join-Path ([IO.Path]::GetDirectoryName($leaf)) ([guid]::NewGuid().ToString('N') + '.m4-probe')
        $renamed = $temporary + '.renamed'
        try {
            [IO.File]::Copy($leaf, $temporary, $false)
            [IO.File]::AppendAllText($temporary, ' editable fixture copy')
            [IO.File]::Move($temporary, $renamed)
            [IO.File]::Delete($renamed)
        } finally {
            if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
            if ([IO.File]::Exists($renamed)) { [IO.File]::Delete($renamed) }
        }
        $denied = $false
        try { $handle = [IO.File]::Open($source, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::Read); $handle.Dispose() }
        catch [UnauthorizedAccessException] { $denied = $true }
        if (!$denied) { throw 'Student can write the shared Default source.' }
        if ((Hash $leaf) -ne $ExpectedHashes[$relative] -or (Hash $source) -ne $ExpectedHashes[$relative]) { throw 'Original fixture changed.' }
        $checks += @{ name=($relative + ':copy-edit-rename-delete-and-default-read-only'); ok=$true }
    }
    $result = @{ ok=$true; checks=$checks }
} catch { $result = @{ ok=$false; checks=$checks; error=$_.Exception.GetType().Name; detail=$_.Exception.Message } }
$path = Join-Path $Root 'output\result.json'
[IO.File]::WriteAllText(($path + '.tmp'), ($result | ConvertTo-Json -Depth 5))
[IO.File]::Move(($path + '.tmp'), $path)
