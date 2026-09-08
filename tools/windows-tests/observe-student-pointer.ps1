param([Parameter(Mandatory=$true)][string]$ExpectedStudentSid)
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=New-Object Security.Principal.WindowsPrincipal($identity)
$hasAdminSid=@($identity.Groups|Where-Object Value -eq 'S-1-5-32-544').Count -ne 0
if($hasAdminSid -or $identity.IsSystem -or $identity.User.Value -ne $ExpectedStudentSid -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -or $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Exact standard student session required'}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class M4CursorObserver {
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
 [DllImport("user32.dll",SetLastError=true)] public static extern bool GetCursorPos(out Point point);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
}
'@
$point=New-Object M4CursorObserver+Point
if(-not [M4CursorObserver]::GetCursorPos([ref]$point)){throw 'Cursor read failed'}
[pscustomobject]@{x=$point.X;y=$point.Y;width=[M4CursorObserver]::GetSystemMetrics(0);height=[M4CursorObserver]::GetSystemMetrics(1);student_session_verified=$true}|ConvertTo-Json -Compress
