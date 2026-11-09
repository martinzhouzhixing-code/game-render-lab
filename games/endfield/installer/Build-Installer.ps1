[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CandidateDirectory,
    [Parameter(Mandatory = $true)] [string] $OutputPath,
    [string] $RuntimeArchive,
    [string] $ReShadeInstaller
)

$ErrorActionPreference = 'Stop'
$CandidateDirectory = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$generic = Join-Path $CandidateDirectory 'renodx-dlss5.addon64'
$mfg = Join-Path $CandidateDirectory 'renodx-mfgunlock.addon64'
$genericHash = (Get-FileHash -LiteralPath $generic -Algorithm SHA256).Hash.ToLowerInvariant()
$mfgHash = (Get-FileHash -LiteralPath $mfg -Algorithm SHA256).Hash.ToLowerInvariant()
if ($genericHash -eq '8790e0d3cad277861c198d1f76bd9e9fc3564ab226b243787d59b73e0c7ce7ca' -or
    $mfgHash -eq 'a9d88b5403f8d4be3de8de59cbb257fb32ea14a1777065eb2a81b118242f796e' -or
    $genericHash -eq $mfgHash) {
    throw 'Embed both matching localized candidate binaries. Unmodified upstream or duplicate binaries are rejected.'
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The Windows .NET Framework 4.x compiler is not available.'
}
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$generated = Join-Path $outputDirectory ('BuildPayload-' + [Guid]::NewGuid().ToString('N') + '.cs')
$resources = [System.Collections.Generic.List[string]]::new()
$resources.Add('/resource:' + $generic + ',candidate/renodx-dlss5.addon64')
$resources.Add('/resource:' + $mfg + ',candidate/renodx-mfgunlock.addon64')

function Add-OptionalResource([string] $Path, [string] $Resource, [string] $Hash) {
    if (!$Path) { return }
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if ((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash -ne $Hash) {
        throw ('Unexpected SHA-256 for ' + $Resource)
    }
    $resources.Add('/resource:' + $resolved + ',' + $Resource)
}

Add-OptionalResource $RuntimeArchive 'offline-runtime.zip' 'a4bc27f82fb83433fd5bf7243e2b598449b98e79d8f8a5acb3516f012d322985'
Add-OptionalResource $ReShadeInstaller 'offline-reshade.exe' 'afe4c8f13048306307983b8b3d41d5bf00a86820440b0e57dea10950e1176445'

try {
    @"
namespace GameRenderLab.Endfield
{
    internal static class BuildPayload
    {
        internal const string GenericHash = "$genericHash";
        internal const string MfgHash = "$mfgHash";
    }
}
"@ | Set-Content -LiteralPath $generated -Encoding UTF8
    $references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Management.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll') |
        ForEach-Object { '/reference:' + $_ }
    $sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName
    & $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /warn:4 ("/out:" + $OutputPath) @references @resources @sources $generated
    if ($LASTEXITCODE -ne 0) { throw "Compiler returned $LASTEXITCODE" }
    Write-Output $OutputPath
}
finally {
    if (Test-Path -LiteralPath $generated -PathType Leaf) { Remove-Item -LiteralPath $generated }
}
