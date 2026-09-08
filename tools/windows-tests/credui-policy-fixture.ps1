param([Parameter(Mandatory=$true)][ValidateSet('Apply','Restore')][string]$Mode)
$ErrorActionPreference='Stop'
if(-not [Security.Principal.WindowsIdentity]::GetCurrent().IsSystem){throw 'SYSTEM required'}
$installation=Get-Content 'C:\ProgramData\LabControl\installation.json'|ConvertFrom-Json
if($installation.installation_id -ne 'b2e98b07-9256-4823-9631-f350c46b014e'){throw 'Installation changed'}
$root='C:\ProgramData\LabControl.NativeSmoke.UacPolicy'
$node=Get-Item $root
if($node.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse fixture refused'}
$acl=Get-Acl $root
if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Untrusted fixture owner'}
foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Fixture not private'}}
$statePath=Join-Path $root 'credui.json'
$keyPath='SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\CredUI'
$name='EnumerateAdministrators'
if($Mode -eq 'Apply'){
 if(Test-Path $statePath){throw 'Existing fixture requires inspection'}
 $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($keyPath)
 $existed=$null -ne $key;$kind='Absent';$value=$null
 if($key){try{if($key.GetValueNames() -contains $name){$kind=$key.GetValueKind($name).ToString();if($kind -ne 'DWord'){throw 'Unsupported original policy kind'};$value=[int]$key.GetValue($name)}}finally{$key.Dispose()}}
 $record=[ordered]@{schema=1;key_existed=$existed;kind=$kind;original=$value;applied=0}
 $bytes=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Compress))
 $file=[IO.FileStream]::new($statePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
 $key=[Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($keyPath)
 try{if(($kind -eq 'Absent' -and $key.GetValueNames() -contains $name) -or ($kind -eq 'DWord' -and ($key.GetValueKind($name).ToString() -ne 'DWord' -or [int]$key.GetValue($name) -ne $value))){throw 'Concurrent policy edit'};$key.SetValue($name,0,[Microsoft.Win32.RegistryValueKind]::DWord);if([int]$key.GetValue($name) -ne 0){throw 'Readback failed'}}finally{$key.Dispose()}
 '{"fixture_credential_enumeration_disabled":true,"production_setting":false}'
}else{
 $record=Get-Content $statePath|ConvertFrom-Json
 if($record.schema -ne 1 -or $record.kind -notin @('Absent','DWord') -or $record.applied -ne 0){throw 'Invalid fixture state'}
 $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($keyPath,$true)
 if(-not $key){throw 'Fixture policy disappeared'}
 $empty=$false
 try{if($key.GetValueKind($name).ToString() -ne 'DWord' -or [int]$key.GetValue($name) -ne 0){throw 'Later policy edit preserved'};if($record.kind -eq 'Absent'){$key.DeleteValue($name)}else{$key.SetValue($name,[int]$record.original,[Microsoft.Win32.RegistryValueKind]::DWord)};$empty=$key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0}finally{$key.Dispose()}
 if(-not $record.key_existed -and $empty){[Microsoft.Win32.Registry]::LocalMachine.DeleteSubKey($keyPath,$false)}
 '{"fixture_credential_enumeration_restored":true,"evidence_retained":true}'
}
