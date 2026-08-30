[CmdletBinding()]
param([Parameter(Mandatory)][string] $Executable)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

if (-not ('NssmManagerNativeResourceProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class NssmManagerNativeResourceProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint FormatMessage(uint flags, IntPtr source, uint id, uint language, StringBuilder buffer, int size, IntPtr arguments);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr module);
}
'@
}

$module = [NssmManagerNativeResourceProbe]::LoadLibraryEx($Executable, [IntPtr]::Zero, 2)
if ($module -eq [IntPtr]::Zero) { throw "LoadLibraryEx failed for '$Executable': $([Runtime.InteropServices.Marshal]::GetLastWin32Error())." }
try {
    $message = [Text.StringBuilder]::new(4096)
    $length = [NssmManagerNativeResourceProbe]::FormatMessage(0x800, $module, 0x400001F5, 0, $message, $message.Capacity, [IntPtr]::Zero)
    if ($length -eq 0 -or -not $message.ToString().StartsWith('NSSM: The non-sucking service manager', [StringComparison]::Ordinal)) {
        throw "The NSSM MessageTable resource is missing or invalid: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())."
    }
}
finally { [NssmManagerNativeResourceProbe]::FreeLibrary($module) | Out-Null }

$version = (Get-Item -LiteralPath $Executable).VersionInfo
if ($version.FileVersion -ne '2.24-101-g897c7ad' -or $version.OriginalFilename -ne 'nssm-manager.exe') {
    throw "The nssm-manager VERSIONINFO resource is invalid: FileVersion='$($version.FileVersion)', OriginalFilename='$($version.OriginalFilename)'."
}

[pscustomobject]@{
    executable = $Executable
    messageTable = $true
    fileVersion = $version.FileVersion
    originalFilename = $version.OriginalFilename
} | Format-List
