# Runs the headless Harmony patch audit (tools\PatchAudit) over every mod's newest build output.
#
# Run this after every game update, right after refreshing AIRL\libs and rebuilding:
#   .\tools\patch-audit.ps1            # audit everything
#   .\tools\patch-audit.ps1 -Verbose   # also list every patch that bound successfully
#   .\tools\patch-audit.ps1 -Only SkillWeb,Chronicle
#
# A clean compile only proves the mods still build. This proves their Harmony patches still BIND
# to the new game assembly (targets exist, overloads are unambiguous, parameter names and
# ___private fields still match). See tools\PatchAudit\Program.cs for the full list of checks.
#
# ASCII only, deliberately: Windows PowerShell 5.1 reads this file as ANSI.

[CmdletBinding()]
param(
    [string[]]$Only,
    [switch]$IncludeRetired,   # also audit the projects listed in $retired below
    [string]$Configuration = 'Debug',
    [string]$Game = 'C:\Program Files (x86)\Steam\steamapps\common\AI Roguelike'
)

# Projects that are not part of the maintained set (same exclusions as package.ps1). They still
# have .csproj files on disk, so without this list every run would report their known breakage.
$retired = @(
    'AIROG_FontModifierMain',   # stale nested duplicate under AIROG_FontSelection\
    'AIRL_TokenCount',          # superseded by AIROG_TokenModifierPlugin in the same folder
    'AIROG_HistoryTab'          # does not compile since the 07/28 MainMenu refactor; not deployed
)

$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
# Unique per run, so two audits at once (or an audit during a packaging run) cannot delete each
# other's staged DLLs.
$stage = Join-Path $env:TEMP ("airog_patch_audit_{0}" -f $PID)

# Build the auditor itself - only when it is missing or older than its sources, so concurrent
# audits do not fight over the .exe.
$auditDir  = Join-Path $root 'tools\PatchAudit'
$auditProj = Join-Path $auditDir 'PatchAudit.csproj'
$exe       = Join-Path $auditDir 'bin\Debug\PatchAudit.exe'
$newestSrc = (Get-ChildItem $auditDir -File | Where-Object { $_.Extension -in '.cs', '.csproj' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
if (-not (Test-Path $exe) -or (Get-Item $exe).LastWriteTime -lt $newestSrc) {
    $null = & dotnet build $auditProj -c Debug -v quiet --nologo "-p:GameDir=$Game" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "PatchAudit failed to build. Run: dotnet build `"$auditProj`"" }
}

# Every mod assembly = every .csproj outside Archived / AIRL / tools. Output paths are not
# uniform (several projects funnel into AIROG_GenContext\bin, some land in net472\net472), so
# resolve each assembly by name and take the newest build.
$names = @()
# Tests\ holds mods' own console test harnesses: not plugins, never audited.
Get-ChildItem $root -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '\\(Archived|AIRL|Inspiration|dist|tools|obj|bin|Tests)\\' } |
    ForEach-Object {
        # One malformed project file must not take the whole audit down with it.
        try {
            $xml = [xml](Get-Content $_.FullName)
            $asm = $xml.SelectSingleNode('//AssemblyName')
            if ($asm) { $names += $asm.InnerText } else { $names += $_.BaseName }
        }
        catch {
            Write-Host ("  could not read {0} (skipped): {1}" -f $_.Name, $_.Exception.Message) -ForegroundColor Yellow
        }
    }
$names = @($names | Sort-Object -Unique)
if (-not $IncludeRetired) { $names = @($names | Where-Object { $retired -notcontains $_ }) }
$selected = $names
if ($Only) {
    $selected = @($names | Where-Object { $n = $_; ($Only | Where-Object { $n -eq $_ -or $n -eq "AIROG_$_" }).Count -gt 0 })
    if ($selected.Count -eq 0) { throw "No mod matches -Only $($Only -join ','). Known: $($names -join ', ')" }
}

# The audited DLLs go in the stage root. Every OTHER mod DLL goes in deps\ and is only probed
# for references: auditing just SkillWeb still needs AIROG_GenContext.dll to load its types.
$depsDir = Join-Path $stage 'deps'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $depsDir -Force | Out-Null

$stale = @()
foreach ($name in $names) {
    $isSelected = $selected -contains $name
    $hit = Get-ChildItem -Path $root -Recurse -Filter "$name.dll" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\bin\\$Configuration\\" -and $_.FullName -notmatch '\\(Archived|tools|dist)\\' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $hit) {
        if ($isSelected) { Write-Host "  no build output for $name (skipped)" -ForegroundColor Yellow }
        continue
    }
    if ($isSelected) { $dest = $stage } else { $dest = $depsDir }
    Copy-Item $hit.FullName (Join-Path $dest "$name.dll") -Force
    if ($isSelected -and $hit.LastWriteTime -lt (Get-Item (Join-Path $root 'AIRL\libs\Assembly-CSharp.dll')).LastWriteTime) { $stale += $name }
}
if ($stale.Count -gt 0) {
    Write-Host ("  NOTE: built BEFORE the current Assembly-CSharp.dll (rebuild first): {0}" -f ($stale -join ', ')) -ForegroundColor Yellow
}

$auditArgs = @($stage, '--libs', (Join-Path $root 'AIRL\libs'), '--core', (Join-Path $Game 'BepInEx\core'), '--probe', $depsDir)
if ($VerbosePreference -ne 'SilentlyContinue') { $auditArgs += '--verbose' }
& $exe @auditArgs
$failures = $LASTEXITCODE
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
if ($failures -eq 0) { Write-Host 'Patch audit clean.' -ForegroundColor Green }
else { Write-Host "Patch audit: $failures failure(s)." -ForegroundColor Red }
exit $failures
