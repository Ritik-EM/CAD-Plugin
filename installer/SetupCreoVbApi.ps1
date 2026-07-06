<#
.SYNOPSIS
    One-time machine setup for the Atlas Creo add-in (VB API / pfcls async COM).

.DESCRIPTION
    Creo's VB API talks to Creo Parametric over an out-of-process COM server,
    pfclscom.exe. A stock Creo install ships that server but does NOT always
    register it for COM, so client apps fail with REGDB_E_CLASSNOTREG /
    TYPE_E_LIBNOTREGISTERED. This script makes the VB API usable by:

      1. Generating lib\Interop.pfcls.dll from the installed Creo (TlbImp) so the
         AtlasCreoAddin / AtlasCreoSpike projects compile.
      2. Registering the pfcls type library (embedded in pfclscom.exe) for COM
         interface marshaling.
      3. Registering a CLSID\LocalServer32 -> pfclscom.exe for every pfcls
         coclass, so `new CCpfc*()` activates.
      4. Setting the PRO_COMM_MSG_EXE machine environment variable, which the VB
         API requires to broker messages to/from Creo.

    Re-run this whenever the Creo version changes. Reversible with -Unregister.

    MUST be run from an ELEVATED PowerShell (writes HKLM + machine env var).

.PARAMETER CreoCommonFiles
    Path to "<creo_loadpoint>\<datecode>\Common Files". Auto-detected under
    C:\Program Files\PTC if omitted (highest version wins).

.PARAMETER Unregister
    Undo COM registration (typelib + coclass LocalServer32 entries).

.EXAMPLE
    # From an elevated PowerShell:
    .\SetupCreoVbApi.ps1

.EXAMPLE
    .\SetupCreoVbApi.ps1 -CreoCommonFiles "C:\Program Files\PTC\Creo 12.4.2.0\Common Files"
#>
[CmdletBinding()]
param(
    [string]$CreoCommonFiles,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot          # ..\ from installer\
$interopPath = Join-Path $repoRoot 'lib\Interop.pfcls.dll'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
        throw "This script must be run from an ELEVATED PowerShell (Run as Administrator)."
    }
}

function Find-CreoCommonFiles {
    if ($CreoCommonFiles) { return $CreoCommonFiles }
    $candidates = Get-ChildItem 'C:\Program Files\PTC' -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'Creo *' } |
        ForEach-Object { Join-Path $_.FullName 'Common Files' } |
        Where-Object { Test-Path (Join-Path $_ 'x86e_win64\obj\pfclscom.exe') }
    if (-not $candidates) { throw "Could not auto-detect Creo. Pass -CreoCommonFiles explicitly." }
    # Highest version last (string sort is good enough for 'Creo 10' < 'Creo 12').
    return ($candidates | Sort-Object)[-1]
}

function Get-Pfcls-Server([string]$common) {
    $exe = Join-Path $common 'x86e_win64\obj\pfclscom.exe'
    if (-not (Test-Path $exe)) { throw "pfclscom.exe not found at $exe" }
    return $exe
}

# ---- TlbImp: (re)generate the interop from the installed Creo -----------------
function Build-Interop([string]$serverExe) {
    $tlbimp = Get-ChildItem 'C:\Program Files (x86)\Microsoft SDKs\Windows' -Recurse -Filter 'TlbImp.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'NETFX 4' -and $_.FullName -notmatch '\\x64\\' } |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $tlbimp) { throw "TlbImp.exe (NETFX 4.x Tools) not found. Install a Windows SDK / VS." }
    New-Item -ItemType Directory -Force -Path (Split-Path $interopPath) | Out-Null
    Write-Host "Generating $interopPath from $serverExe ..."
    & $tlbimp $serverExe /out:$interopPath /namespace:pfcls /machine:Agnostic /silent
    if (-not (Test-Path $interopPath)) { throw "TlbImp did not produce $interopPath" }
    Write-Host "  interop OK"
}

# ---- Type library registration (embedded in pfclscom.exe) ---------------------
Add-Type -Namespace Atlas -Name Ole -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("oleaut32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, PreserveSig=false)]
    public static extern void LoadTypeLibEx(string strTypeLibName, int regKind, out System.IntPtr typeLib);
'@ -ErrorAction SilentlyContinue

function Register-TypeLib([string]$serverExe) {
    Write-Host "Registering pfcls type library ..."
    $tl = [IntPtr]::Zero
    try { [Atlas.Ole]::LoadTypeLibEx($serverExe, 1, [ref]$tl) } catch { }  # 1 = REGKIND_REGISTER
    # The side effect (registry write) completes even though marshaling the
    # returned ITypeLib may throw; verify by presence of the TypeLib key.
    $asm = [Reflection.Assembly]::LoadFile($interopPath)
    $tlbGuid = [System.Runtime.InteropServices.Marshal]::GetTypeLibGuidForAssembly($asm).ToString()
    if (Test-Path "HKLM:\SOFTWARE\Classes\TypeLib\{$tlbGuid}") { Write-Host "  typelib OK" }
    else { Write-Warning "  typelib key not found ({$tlbGuid}) — COM activation may still fail." }
}

# ---- CLSID\LocalServer32 for every pfcls coclass ------------------------------
function Register-CoClasses([string]$serverExe) {
    $asm = [Reflection.Assembly]::LoadFile($interopPath)
    $coclasses = $asm.GetTypes() | Where-Object { $_.IsClass -and $_.Name -like 'CCpfc*Class' -and $_.GUID -ne [Guid]::Empty }
    $n = 0
    foreach ($t in $coclasses) {
        $g = $t.GUID.ToString().ToUpper()
        $base = "HKLM:\SOFTWARE\Classes\CLSID\{$g}"
        if ($Unregister) {
            if (Test-Path $base) { Remove-Item $base -Recurse -Force; $n++ }
            continue
        }
        New-Item -Path $base -Force | Out-Null
        Set-ItemProperty -Path $base -Name '(default)' -Value ($t.Name -replace 'Class$','')
        New-Item -Path "$base\LocalServer32" -Force | Out-Null
        Set-ItemProperty -Path "$base\LocalServer32" -Name '(default)' -Value "`"$serverExe`""
        $n++
    }
    if ($Unregister) { Write-Host "  removed $n coclass CLSID keys" }
    else { Write-Host "  registered $n coclass LocalServer32 entries" }
}

# ---- PRO_COMM_MSG_EXE (machine env var) ---------------------------------------
function Set-CommMsgEnv([string]$common) {
    $exe = Join-Path $common 'x86e_win64\obj\pro_comm_msg.exe'
    if (-not (Test-Path $exe)) { Write-Warning "pro_comm_msg.exe not found at $exe"; return }
    [Environment]::SetEnvironmentVariable('PRO_COMM_MSG_EXE', $exe, 'Machine')
    Write-Host "Set PRO_COMM_MSG_EXE = $exe (Machine)"
}

# ---- main ---------------------------------------------------------------------
Assert-Admin
$common = Find-CreoCommonFiles
$server = Get-Pfcls-Server $common
Write-Host "Creo Common Files : $common"
Write-Host "pfcls COM server  : $server"
Write-Host ""

if ($Unregister) {
    if (-not (Test-Path $interopPath)) { throw "Need $interopPath to know which CLSIDs to remove; regenerate first." }
    Register-CoClasses $server
    Write-Host "Unregister complete. (Type library + PRO_COMM_MSG_EXE left in place.)"
    return
}

if (-not (Test-Path $interopPath)) { Build-Interop $server } else { Write-Host "Interop present: $interopPath" }
Register-TypeLib $server
Register-CoClasses $server
Set-CommMsgEnv $common
Write-Host ""
Write-Host "Done. Open Creo Parametric with an assembly, then run AtlasCreoAddin.exe."
