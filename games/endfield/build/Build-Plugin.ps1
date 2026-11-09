[CmdletBinding()]
param(
    [string]$DependencyDirectory,
    [string]$OutputDirectory,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$plugin = Join-Path $PSScriptRoot '..\plugin'
$source = Join-Path $plugin 'src\unified-20261011'
$generic = Join-Path $source 'generic'
$mfg = Join-Path $source 'mfg'
if (-not $DependencyDirectory) { $DependencyDirectory = Join-Path $repository '.build\endfield-dependencies' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'dist\endfield-candidate' }
$DependencyDirectory = [IO.Path]::GetFullPath($DependencyDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$intermediate = Join-Path $repository '.build\endfield-native'

function Invoke-Checked {
    param([Parameter(Mandatory)][string]$Tool, [Parameter(Mandatory)][string[]]$Arguments)
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Tool exited with code $LASTEXITCODE." }
}

if (-not (Test-Path -LiteralPath (Join-Path $generic 'src\addons\dlss5\addon.cpp') -PathType Leaf)) {
    throw 'Endfield Generic source is missing from games/endfield/plugin.'
}

if (-not (Get-Command cl.exe -CommandType Application -ErrorAction SilentlyContinue) -or
    -not (Get-Command rc.exe -CommandType Application -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio vswhere is missing.' }
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'An x64 Visual C++ toolset is required.' }
    $env:VSCMD_SKIP_SENDTELEMETRY = '1'
    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -DevCmdArguments '-arch=x64 -host_arch=x64' -SkipAutomaticLocation
}
if ($env:VSCMD_ARG_TGT_ARCH -and $env:VSCMD_ARG_TGT_ARCH -ne 'x64') {
    throw 'Start the x64 Visual Studio Developer PowerShell.'
}

$cl = (Get-Command cl.exe -CommandType Application).Source
$rc = (Get-Command rc.exe -CommandType Application).Source
$dumpbin = (Get-Command dumpbin.exe -CommandType Application).Source
$python = (Get-Command python.exe -CommandType Application).Source
Invoke-Checked $python @('-B', (Join-Path $PSScriptRoot 'stage_dependencies.py'), $DependencyDirectory)
New-Item -ItemType Directory -Path $intermediate, $OutputDirectory -Force | Out-Null

$ngx = Join-Path $DependencyDirectory 'external\DLSS\include'
$detours = Join-Path $mfg 'deps\detours'
$detoursLib = Join-Path $detours 'lib.X64\detours.lib'
$detoursInclude = Join-Path $detours 'include'
$reshadeVendor = Join-Path $mfg 'deps\reshade'
$genericIncludes = @(
    (Join-Path $generic 'generated'),
    $ngx,
    $detoursInclude,
    (Join-Path $DependencyDirectory 'external\json\include'),
    (Join-Path $DependencyDirectory 'external\frozen\include'),
    (Join-Path $DependencyDirectory 'external\gtl\include'),
    (Join-Path $DependencyDirectory 'external\Streamline\include'),
    (Join-Path $DependencyDirectory 'external\reshade'),
    $reshadeVendor,
    (Join-Path $DependencyDirectory 'renodx\include'),
    (Join-Path $DependencyDirectory 'dxc\include\dxc')
)
$mfgIncludes = @(
    $reshadeVendor,
    (Join-Path $mfg 'deps\streamline'),
    $ngx,
    (Join-Path $mfg 'deps\nvapi'),
    $detoursInclude
)
foreach ($directory in ($genericIncludes + $mfgIncludes)) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Missing include directory: $directory" }
}
if (-not (Test-Path -LiteralPath $detoursLib -PathType Leaf)) { throw "Missing vendored Detours library: $detoursLib" }
$genericIncFlags = @($genericIncludes | ForEach-Object { "/I$_" })
$mfgIncFlags = @($mfgIncludes | ForEach-Object { "/I$_" })
$common = @('/nologo', '/std:c++20', '/EHsc', '/utf-8', '/O2', '/MT', '/bigobj', '/DNOMINMAX')
$genericRes = Join-Path $intermediate 'generic.res'
$mfgRes = Join-Path $intermediate 'mfg.res'
$detoursObj = Join-Path $intermediate 'detours-small.obj'
$genericBinary = Join-Path $intermediate 'renodx-dlss5.addon64'
$mfgBinary = Join-Path $intermediate 'renodx-mfgunlock.addon64'

Push-Location -LiteralPath $intermediate
try {
    Invoke-Checked $rc @('/nologo', '/fo', $genericRes, (Join-Path $generic 'generated\resource.rc'))
    Invoke-Checked $cl ($common + $genericIncFlags + @(
        '/DNDEBUG', '/LD', (Join-Path $generic 'src\addons\dlss5\addon.cpp'),
        "/Fo$(Join-Path $intermediate 'generic.obj')", "/Fe$genericBinary", '/link',
        $genericRes, $detoursLib, 'kernel32.lib', 'user32.lib', 'gdi32.lib',
        'winspool.lib', 'shell32.lib', 'ole32.lib', 'oleaut32.lib', 'uuid.lib',
        'comdlg32.lib', 'advapi32.lib', 'bcrypt.lib', '/INCREMENTAL:NO'
    ))
    Invoke-Checked $cl @(
        '/nologo', '/c', '/std:c++17', '/EHsc', '/O2', '/MT', "/I$detoursInclude",
        (Join-Path $detours 'src\detours.cpp'), "/Fo$detoursObj"
    )
    Invoke-Checked $rc @('/nologo', '/fo', $mfgRes, (Join-Path $mfg 'version.rc'))
    $mfgLink = @($detoursLib, 'version.lib', 'user32.lib', 'advapi32.lib', '/INCREMENTAL:NO')
    Invoke-Checked $cl ($common + $mfgIncFlags + @(
        '/LD', (Join-Path $mfg 'addon\addon.cpp'), $detoursObj,
        "/Fo$(Join-Path $intermediate 'mfg.obj')", "/Fe$mfgBinary", '/link', $mfgRes
    ) + $mfgLink)
    if (-not $SkipTests) {
        $testBinary = Join-Path $intermediate 'native-policy.exe'
        Invoke-Checked $cl ($common + $mfgIncFlags + @(
            (Join-Path $mfg 'tests\native-policy.cpp'), $detoursObj,
            "/Fo$(Join-Path $intermediate 'native-policy.obj')", "/Fe$testBinary", '/link'
        ) + $mfgLink)
        & $testBinary
        if ($LASTEXITCODE -ne 0) { throw 'MFG native policy test failed.' }
        $startupTest = Join-Path $intermediate 'Endfield.exe'
        Copy-Item -LiteralPath $testBinary -Destination $startupTest -Force
        Invoke-Checked $startupTest @('--startup-policy')
    }
} finally {
    Pop-Location
}

$expectedExports = @{
    $genericBinary = @('NAME', 'DESCRIPTION', 'DLSS5StageProtocolVersion', 'DLSS5GetStagePlan', 'DLSS5VulkanPrePresentV1')
    $mfgBinary = @('NAME', 'DESCRIPTION', 'MfgUnlockDrawSimpleControlsV1', 'MfgUnlockGetDiagnosticState')
}
$hashes = [ordered]@{}
foreach ($binary in @($genericBinary, $mfgBinary)) {
    $headers = & $dumpbin /headers $binary | Out-String
    if ($LASTEXITCODE -ne 0 -or $headers -notmatch '(?im)8664 machine \(x64\)') {
        throw "Not a Windows x64 PE: $binary"
    }
    $exports = & $dumpbin /exports $binary | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Could not read PE exports: $binary" }
    foreach ($symbol in $expectedExports[$binary]) {
        if ($exports -notmatch ('(?m)\b' + [regex]::Escape($symbol) + '\s*$')) {
            throw "Missing required export $symbol in $binary"
        }
    }
}

foreach ($binary in @($genericBinary, $mfgBinary)) {
    $name = [IO.Path]::GetFileName($binary)
    $published = Join-Path $OutputDirectory $name
    Copy-Item -LiteralPath $binary -Destination $published -Force
    $hashes[$name] = (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash.ToLowerInvariant()
}
$git = Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue
$revision = $null
if ($git -and (Test-Path -LiteralPath (Join-Path $repository '.git'))) {
    $revision = & $git.Source -C $repository rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine source revision.' }
}
$manifest = [ordered]@{
    schema = 1
    source_commit = $revision
    platform = 'windows-x64'
    sha256 = $hashes
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'candidate-manifest.json') -Encoding utf8
Write-Host "Built and verified the Endfield Chinese UI add-on pair in $OutputDirectory"
