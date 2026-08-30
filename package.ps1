# Builds every currently-deployed AIROG mod and assembles a drag-and-drop bundle in dist\:
# BepInEx + its dependencies, the mod DLLs, the game-root loader files, and the StreamingAssets
# each mod needs - all laid out so the user drops the bundle's contents straight into the
# AI Roguelike folder. Companion to deploy.ps1 (which pushes DLLs to the live game).
#
# ASCII only, deliberately: Windows PowerShell 5.1 reads this file as ANSI, so a stray em-dash
# or box-drawing character turns into a parser error.
#
# The mod list is deliberately an allow-list of what is ACTUALLY DEPLOYED, not every .csproj in
# the repo - several projects are broken or retired and must not ship. Excluded:
#
#   AIROG_HistoryTab       - disabled in-game (.dll.disabled); stale vs the current build
#   AIROG_DeepgramTTS      - not deployed
#   AIROG_Sapi5            - not deployed (broken by the 06/15 TTS API change)
#   AIROG_PresetExporter   - not deployed
#   AIROG_WomboStyles      - not deployed
#   AIRL_TokenCount        - superseded by AIROG_TokenModifierPlugin in the same folder
#   AIROG_FontModifierMain - duplicate nested copy under AIROG_FontSelection\
#   AIROG_Multiplayer      - retired to Archived\ (the game has native MP now)
#
# When a mod becomes deployable, add it here AND to deploy.ps1.
#
# -Only ships a single mod (or a few) instead of the whole set, for handing one person the one
# thing they asked for. It still bundles BepInEx, and pulls in that mod's dependencies by reading
# its <ProjectReference> entries, so the result is self-sufficient:
#
#   .\package.ps1 -Only ScenePace          -> BepInEx + AIROG_Core + AIROG_ScenePace  (~0.6 MB)
#   .\package.ps1 -Only Settlement,SkillWeb
#
# The selection also narrows the StreamingAssets that get bundled, gets its own stage folder and
# '-ModName' zip suffix, and swaps in a single-mod READ ME. The full bundle is unaffected.
#
# NOTE: BepInEx\config is NEVER bundled. Several live .cfg files hold real API keys
# (Gemini, Deepgram). BepInEx regenerates defaults on first run, so shipping them buys
# nothing and would leak credentials.

param(
    [switch]$SkipBuild,       # collect existing build output without rebuilding
    [switch]$IncludeMusic,    # add the ~266 MB MusicExpansion library to the bundle
    [switch]$NoZip,           # leave dist\ staged but don't archive
    [string[]]$Only,          # ship just these mods (plus their deps + BepInEx), e.g. -Only ScenePace
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$game  = 'C:\Program Files (x86)\Steam\steamapps\common\AI Roguelike'
$dist  = Join-Path $root 'dist'

# Assembly name => project file, relative to repo root.
$mods = [ordered]@{
    'AIROG_Core'                = 'AIROG_Core\AIROG_Core.csproj'
    'AIROG_GenContext'          = 'AIROG_GenContext\AIROG_GenContext.csproj'
    'AIROG_UnifiedBridge'       = 'AIROG_UnifiedBridge\AIROG_UnifiedBridge.csproj'
    'AIROG_ALife'               = 'AIROG_ALife\AIROG_ALife.csproj'
    'AIROG_Chronicle'           = 'AIROG_Chronicle\AIROG_Chronicle.csproj'
    'AIROG_DirectedUpdates'     = 'AIROG_DirectedUpdates\AIROG_DirectedUpdates.csproj'
    'AIROG_FontModifier'        = 'AIROG_FontModifierMain\AIROG_FontModifier.csproj'
    'AIROG_GCHelper'            = 'AIROG_GCHelper\AIROG_GCHelper.csproj'
    'AIROG_GrandStrategy'       = 'AIROG_GrandStrategy\AIROG_GrandStrategy.csproj'
    'AIROG_Insight'             = 'AIROG_Insight\AIROG_Insight.csproj'
    'AIROG_LoopBeGone'          = 'AIROG_LoopBeGone\AIROG_LoopBeGone.csproj'
    'AIROG_MusicExpansion'      = 'AIROG_MusicExpansion\AIROG_MusicExpansion.csproj'
    'AIROG_Mythic'              = 'AIROG_Mythic\AIROG_Mythic.csproj'
    'AIROG_NanoBanana'          = 'AIROG_NanoBanana\AIROG_NanoBanana.csproj'
    'AIROG_NPCExpansion'        = 'AIROG_NPCExpansion\AIROG_NPCExpansion.csproj'
    'AIROG_OpenAI5'             = 'AIROG_OpenAI5\AIROG_OpenAI5.csproj'
    'AIROG_OpenAIImage'         = 'AIROG_OpenAIImage\AIROG_OpenAIImage.csproj'
    'AIROG_RandomOrg'           = 'AIROG_RandomOrg\AIROG_RandomOrg.csproj'
    'AIROG_Reverie'             = 'AIROG_Reverie\AIROG_Reverie.csproj'
    'AIROG_ScenePace'           = 'AIROG_ScenePace\AIROG_ScenePace.csproj'
    'AIROG_Settlement'          = 'AIROG_Settlement\AIROG_Settlement.csproj'
    'AIROG_SkillWeb'            = 'AIROG_SkillWeb\AIROG_SkillWeb.csproj'
    'AIROG_TokenModifierPlugin' = 'AIROG_TokenCount\AIRL_TokenCount\AIROG_TokenModifierPlugin.csproj'
    'AIROG_VertexAI'            = 'AIROG_VertexAI\AIROG_VertexAI.csproj'
    'AIROG_WorldExpansion'      = 'AIROG_WorldExpansion\AIROG_WorldExpansion.csproj'
    'StableHordeDetector'       = 'AIROG_StableHordeDetector\StableHordeDetector.csproj'
}

# Loader files that live in the game root next to the .exe.
$rootFiles = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')

# Which mod owns which StreamingAssets folder. Keyed by mod so that -Only ships exactly the
# assets its selection needs; the full bundle still resolves to the same six (plus Music).
# Sourced from the live game rather than the repo: that is the known-good deployed state, and
# Fonts\myfonts + Music exist nowhere else.
$assetsOwnedBy = @{
    'AIROG_Chronicle'      = 'Chronicle'
    'AIROG_FontModifier'   = 'Fonts'
    'AIROG_GenContext'     = 'GenContext'
    'AIROG_NPCExpansion'   = 'NPCExpansion'
    'AIROG_Settlement'     = 'Settlement'
    'AIROG_SkillWeb'       = 'SkillWeb'
    'AIROG_MusicExpansion' = 'Music'   # plugin always ships; the audio only with -IncludeMusic
}

# ---- Selection --------------------------------------------------------------
# -Only takes mod names with or without the AIROG_ prefix. Dependencies are read from each
# project's <ProjectReference> entries rather than hardcoded, so a mod that later picks up a
# dependency on GenContext or UnifiedBridge does not silently ship a bundle that cannot load.
function Resolve-ModDeps([string]$name, [hashtable]$byProjLeaf, $modMap, [string]$repoRoot, [System.Collections.Generic.HashSet[string]]$seen) {
    if (-not $seen.Add($name)) { return }
    $proj = Join-Path $repoRoot $modMap[$name]
    if (-not (Test-Path $proj)) { return }
    $xml = [xml](Get-Content $proj)
    foreach ($node in $xml.SelectNodes('//ProjectReference')) {
        $leaf = [IO.Path]::GetFileNameWithoutExtension($node.Include)
        if ($byProjLeaf.ContainsKey($leaf)) {
            Resolve-ModDeps $byProjLeaf[$leaf] $byProjLeaf $modMap $repoRoot $seen
        }
    }
    # Not every mod-to-mod dependency is a <ProjectReference>. Several (ALife, and any other
    # soft-dependency plugin) reference a sibling as a plain <Reference> with a HintPath into
    # that project's bin. Those are still real runtime dependencies: a bundle shipping ALife
    # without GenContext LOADS FINE and then silently does nothing, because the provider has
    # nothing to register with. Resolve them by assembly name against the mod list.
    foreach ($node in $xml.SelectNodes('//Reference')) {
        $asm = $node.Include
        if ($asm -and $modMap.Contains($asm)) {
            Resolve-ModDeps $asm $byProjLeaf $modMap $repoRoot $seen
        }
    }
}

$suffix = ''
if ($Only) {
    $byProjLeaf = @{}
    foreach ($k in $mods.Keys) { $byProjLeaf[[IO.Path]::GetFileNameWithoutExtension($mods[$k])] = $k }

    $wanted = New-Object System.Collections.Generic.HashSet[string]
    foreach ($request in $Only) {
        $match = $mods.Keys | Where-Object { $_ -eq $request -or $_ -eq "AIROG_$request" } | Select-Object -First 1
        if (-not $match) { throw "Unknown mod '$request'. Known: $(($mods.Keys | Sort-Object) -join ', ')" }
        Resolve-ModDeps $match $byProjLeaf $mods $root $wanted
    }

    $picked = [ordered]@{}
    foreach ($k in $mods.Keys) { if ($wanted.Contains($k)) { $picked[$k] = $mods[$k] } }
    $mods = $picked

    # Name the bundle after what was asked for, not after the dependencies dragged in with it.
    $requested = @($Only | ForEach-Object { $_ -replace '^AIROG_', '' })
    $suffix = '-' + ($requested -join '-')
    Write-Host ("Single-mod bundle: {0} (with {1})" -f ($requested -join ', '), (($mods.Keys) -join ', ')) -ForegroundColor Cyan
}

$streamingAssets = @()
foreach ($k in $mods.Keys) {
    if (-not $assetsOwnedBy.ContainsKey($k)) { continue }
    if ($k -eq 'AIROG_MusicExpansion' -and -not $IncludeMusic) { continue }
    $streamingAssets += $assetsOwnedBy[$k]
}
$streamingAssets = @($streamingAssets | Sort-Object)

# The music variant gets its own stage folder and its own '-Music' zip name, so the plain and
# music bundles can be produced back to back without clobbering each other. The suffix is in
# the filename because the two are otherwise indistinguishable apart from a ~266 MB size gap.
# -Only adds its own suffix for the same reason.
if ($IncludeMusic) { $suffix += '-Music' }
$stage = Join-Path $dist "AIROG_Bundle$suffix"

# ---- Build ------------------------------------------------------------------
$buildFailed = @()
if ($SkipBuild) {
    Write-Host "Skipping build; collecting existing $Configuration output." -ForegroundColor Yellow
}
else {
    Write-Host "Building $($mods.Count) mods ($Configuration)..." -ForegroundColor Cyan
    foreach ($name in $mods.Keys) {
        $proj = Join-Path $root $mods[$name]
        if (-not (Test-Path $proj)) {
            Write-Host "  MISSING PROJECT   $name" -ForegroundColor Red
            $buildFailed += $name
            continue
        }
        $null = & dotnet build $proj -c $Configuration -v quiet --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  BUILD FAILED      $name" -ForegroundColor Red
            $buildFailed += $name
        }
        else {
            Write-Host "  built             $name" -ForegroundColor DarkGray
        }
    }
}

# ---- Stage ------------------------------------------------------------------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$pluginDir = Join-Path $stage 'BepInEx\plugins'
$coreDir   = Join-Path $stage 'BepInEx\core'
$saDir     = Join-Path $stage 'AI Roguelite_Data\StreamingAssets'
$dirs = @($pluginDir, $coreDir)
# Skipped when the selection owns no assets, so a single-mod bundle does not ship an empty
# AI Roguelite_Data tree that looks like something failed to copy.
if ($streamingAssets.Count -gt 0) { $dirs += $saDir }
foreach ($d in $dirs) { New-Item -ItemType Directory -Path $d -Force | Out-Null }

# Mod DLLs. Output paths are NOT uniform - several projects funnel into AIROG_GenContext\bin and
# a few land in a doubled net472\net472\ folder - so resolve by search, newest match wins.
$collected = @()
$missing = @()
foreach ($name in $mods.Keys) {
    $hit = Get-ChildItem -Path $root -Recurse -Filter "$name.dll" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\bin\\$Configuration\\" -and $_.FullName -notmatch '\\Archived\\' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if (-not $hit) {
        Write-Host "  NO OUTPUT         $name" -ForegroundColor Red
        $missing += $name
        continue
    }
    Copy-Item $hit.FullName (Join-Path $pluginDir "$name.dll") -Force

    $version = 'unknown'
    try { $version = [Reflection.AssemblyName]::GetAssemblyName($hit.FullName).Version.ToString() } catch {}
    $collected += [PSCustomObject]@{ Mod = $name; Version = $version; Built = $hit.LastWriteTime.ToString('yyyy-MM-dd HH:mm') }
}

# BepInEx runtime (core DLLs + the doorstop loader files in the game root).
Copy-Item (Join-Path $game 'BepInEx\core\*') $coreDir -Recurse -Force
foreach ($f in $rootFiles) {
    $src = Join-Path $game $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $stage $f) -Force }
    else { Write-Host "  MISSING ROOT FILE $f" -ForegroundColor Yellow }
}

# StreamingAssets the mods load at runtime.
foreach ($d in $streamingAssets) {
    $src = Join-Path $game "AI Roguelite_Data\StreamingAssets\$d"
    if (Test-Path $src) { Copy-Item $src $saDir -Recurse -Force }
    else { Write-Host "  MISSING ASSETS    $d" -ForegroundColor Yellow }
}

# ---- Readme + manifest ------------------------------------------------------
$stamp = Get-Date -Format 'yyyy-MM-dd'
if ($Only) {
    # Single-mod bundle: the full bundle's edition/disable notes are noise here, and the
    # dependency list has to be explicit so nobody deletes AIROG_Core and files a bug.
    $deps = @($mods.Keys | Where-Object { $requested -notcontains ($_ -replace '^AIROG_', '') })
    $title = ($requested -join ' + ')
    # Only list the StreamingAssets line when the selection actually ships one, so the
    # 'should look like' block matches what is in the folder.
    $layout = @("    AI Roguelike\BepInEx\plugins\*.dll")
    if ($streamingAssets.Count -gt 0) { $layout += "    AI Roguelike\AI Roguelite_Data\StreamingAssets\..." }
    $layout += "    AI Roguelike\winhttp.dll"
    $readme = @(
        "AIROG_$title - $stamp"
        ""
        "INSTALL"
        "  Drag the contents of this folder into your AI Roguelite root folder"
        "  (the one containing 'AI Roguelite.exe'), overwriting when asked."
        ""
        "  Afterwards the structure should look like:"
    ) + $layout + @(
        ""
        "  BepInEx is included, so a fresh install needs nothing else. If you already"
        "  run other BepInEx mods, only the .dll files in BepInEx\plugins are new -"
        "  the rest will just overwrite with identical files."
        ""
    )
    if ($deps.Count -gt 0) {
        # Core / GenContext / UnifiedBridge are inert plumbing. Anything else dragged in is a
        # real gameplay mod (ALife pulls WorldExpansion, which runs its own world sim), so do
        # not blanket-describe every dependency as a library that does nothing on its own.
        $libs  = @('AIROG_Core', 'AIROG_GenContext', 'AIROG_UnifiedBridge')
        $extra = @($deps | Where-Object { $libs -notcontains $_ })
        if ($deps.Count -eq 1) { $them = 'it' } else { $them = 'them' }
        $readme += @(
            "DEPENDENCIES"
            "  This bundle also contains $($deps -join ', '), which"
            "  AIROG_$title needs in order to work - leave $them in place, and"
            "  overwrite freely if you already have $them."
            ""
        )
        if ($extra.Count -gt 0) {
            $readme += @(
                "  Note: $($extra -join ', ') is a full mod in its own right, not just"
                "  plumbing - it runs its own simulation alongside AIROG_$title."
                ""
            )
        }
    }
    $readme += @(
        "CONFIG"
        "  Settings appear in BepInEx\config after the first launch."
        ""
        "TO UNINSTALL"
        "  Delete the mod's .dll from BepInEx\plugins (or rename it to .dll.disabled)."
        ""
        "See MANIFEST.txt for exact versions in this build."
    )
}
elseif ($IncludeMusic) {
    $edition = @(
        "EDITION: with music"
        "  Includes the AIROG_MusicExpansion audio library"
        "  (AI Roguelite_Data\StreamingAssets\Music). If you already have the"
        "  music installed, the base bundle without '-Music' is the smaller download."
        ""
    )
}
else {
    $edition = @(
        "EDITION: base (no music)"
        "  The AIROG_MusicExpansion plugin is included, but not its audio library."
        "  For the soundtrack, use the '-Music' bundle instead."
        ""
    )
}
if (-not $Only) {
$readme = @(
    "Verinax AIRL BepInEx Mods - $stamp"
    ""
) + $edition + @(
    "INSTALL"
    "  Drag the contents of this folder into your AI Roguelite root folder"
    "  (the one containing 'AI Roguelite.exe'), overwriting when asked."
    ""
    "  Afterwards the structure should look like:"
    "    AI Roguelike\BepInEx\plugins\*.dll"
    "    AI Roguelike\AI Roguelite_Data\StreamingAssets\..."
    "    AI Roguelike\winhttp.dll"
    ""
    "  BepInEx is included, so a fresh install needs nothing else."
    ""
    "TO DISABLE A MOD"
    "  Delete its .dll from BepInEx\plugins, or rename it to .dll.disabled."
    "  AIROG_Core, AIROG_GenContext and AIROG_UnifiedBridge are shared"
    "  dependencies - leave those in place."
    ""
    "CONFIG"
    "  Per-mod settings appear in BepInEx\config after the first launch."
    "  API keys for the AI backends are entered in the in-game Options menu."
    ""
    "See MANIFEST.txt for the exact mod list and versions in this build."
)
}
Set-Content -Path (Join-Path $stage 'READ ME PLEASE.txt') -Value $readme -Encoding utf8

if ($Only) { $manifest = @("AIROG_$title - $stamp", "$($collected.Count) plugins", "") }
else        { $manifest = @("AI Roguelite mod bundle - $stamp", "$($collected.Count) plugins", "") }
$manifest += ($collected | Format-Table -AutoSize | Out-String).TrimEnd()
$manifest += ""
if ($streamingAssets.Count -gt 0) { $manifest += "StreamingAssets included: $($streamingAssets -join ', ')" }
else                              { $manifest += "StreamingAssets included: none (this mod needs none)" }
if (-not $IncludeMusic -and -not $Only) { $manifest += "(MusicExpansion audio omitted - re-run with -IncludeMusic to add it)" }
Set-Content -Path (Join-Path $stage 'MANIFEST.txt') -Value $manifest -Encoding utf8

Write-Host ""
$collected | Format-Table -AutoSize

# ---- Archive ----------------------------------------------------------------
$sizeMb = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
if (-not $NoZip) {
    $zip = Join-Path $dist "Verinax_AIRL_Mods_$stamp$suffix.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Entries are written by hand rather than with Compress-Archive or ZipFile.CreateFromDirectory:
    #   - Compress-Archive opens each file for EXCLUSIVE read and dies on transient Defender
    #     scan locks over the freshly-copied BepInEx core DLLs. FileShare.ReadWrite avoids that.
    #   - CreateFromDirectory writes Windows '\' separators into entry names. The ZIP spec
    #     requires '/', and strict extractors read 'BepInEx\plugins\x.dll' as ONE flat filename,
    #     which would collapse the layout this bundle exists to preserve.
    # ZipArchive lives in System.IO.Compression; ZipFile in .FileSystem. Load both.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipStream = [System.IO.File]::Open($zip, 'Create')
    $archive = New-Object System.IO.Compression.ZipArchive($zipStream, 'Create')
    try {
        foreach ($file in Get-ChildItem $stage -Recurse -File -Force) {
            $rel = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $srcStream = [System.IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite')
            try { $srcStream.CopyTo($entryStream) }
            finally { $srcStream.Close(); $entryStream.Close() }
        }
    }
    finally { $archive.Dispose(); $zipStream.Close() }
    $zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host ("Bundle: {0}  ({1} MB zipped, {2} MB staged)" -f $zip, $zipMb, $sizeMb) -ForegroundColor Green
}
else {
    Write-Host ("Staged (no zip): {0}  ({1} MB)" -f $stage, $sizeMb) -ForegroundColor Green
}

if ($buildFailed.Count -gt 0) { Write-Host ("Build failures: {0}" -f ($buildFailed -join ', ')) -ForegroundColor Red }
if ($missing.Count -gt 0)     { Write-Host ("No build output: {0}" -f ($missing -join ', ')) -ForegroundColor Red }
if ($buildFailed.Count -eq 0 -and $missing.Count -eq 0) { Write-Host ("All {0} mods packaged." -f $collected.Count) -ForegroundColor Green }
