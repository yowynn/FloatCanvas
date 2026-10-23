param([Parameter(Mandatory)][string]$ExecutablePath,[switch]$TraditionalExclusive)
$ErrorActionPreference='Stop'
$executable=(Resolve-Path -LiteralPath $ExecutablePath).Path
if([IO.Path]::GetFileName($executable) -ine 'FullscreenInputFixture.exe'){throw 'Expected the self-owned FullscreenInputFixture.exe.'}
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=$executable
$start.WorkingDirectory=Split-Path -Parent $executable
$start.UseShellExecute=$false
if($TraditionalExclusive){
    # Apply only to this child. No registry/global environment changes. Actual
    # presentation must still be measured; a compatibility request is not proof.
    $prior=$start.EnvironmentVariables['__COMPAT_LAYER']
    $start.EnvironmentVariables['__COMPAT_LAYER']=($prior+' DISABLEDXMAXIMIZEDWINDOWEDMODE').Trim()
}
$process=[Diagnostics.Process]::Start($start)
[pscustomobject]@{Pid=$process.Id;Executable=$executable;TraditionalExclusiveRequested=$TraditionalExclusive.IsPresent;CompatLayer=$start.EnvironmentVariables['__COMPAT_LAYER'];PresentationVerified=$false}
