param(
    [Parameter(Mandatory)][string]$CompilerCli,
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$RequireEquivalent,
    [switch]$RequireNoViewOverhead
)

# Use an output directory outside the checkout. Inputs are an existing core and
# its two SDK assemblies, keeping the native compiler and SDK versions pinned.
$ErrorActionPreference = 'Stop'
$CompilerCli = (Resolve-Path -LiteralPath $CompilerCli).Path
$InputDirectory = (Resolve-Path -LiteralPath $InputDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory so stale artifacts cannot pass the comparison.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$references = @('CopperSFS.Core.dll', 'CopperSharp.Sdk.Amiga.dll', 'CopperSharp.Sdk.Amiga.Support.dll')
$items = foreach ($name in $references) {
    $file = Join-Path $InputDirectory $name
    if (!(Test-Path -LiteralPath $file)) { throw "Missing input: $name" }
    $path = [Security.SecurityElement]::Escape($file)
    "<Reference Include=`"$([IO.Path]::GetFileNameWithoutExtension($name))`"><HintPath>$path</HintPath></Reference>"
}
$source = [Security.SecurityElement]::Escape($PSScriptRoot)
@"
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems><AssemblyName>CopperSFS.WriteQueueViews</AssemblyName></PropertyGroup>
<ItemGroup><Compile Include="$source/*.cs" />$($items -join "`n")</ItemGroup>
</Project>
"@ | Set-Content -LiteralPath "$OutputDirectory/Probe.csproj"
dotnet build "$OutputDirectory/Probe.csproj" -c Release -o "$OutputDirectory/inputs" --verbosity quiet -m:1 *> "$OutputDirectory/build.log"
if ($LASTEXITCODE -ne 0) { Get-Content "$OutputDirectory/build.log"; throw 'Prototype build failed.' }

$results = foreach ($cpu in @('68000', '68020')) {
    $baselines = @{}
    foreach ($variant in @('Raw', 'NativeView', 'BorrowedView', 'RawPointers', 'BorrowedPointers')) {
        $baselineName = if ($variant.EndsWith('Pointers')) { 'RawPointers' } else { 'Raw' }
        $output = "$OutputDirectory/$variant-$cpu"
        $arguments = @($CompilerCli, "$OutputDirectory/inputs/CopperSFS.WriteQueueViews.dll",
            '--entry', "CopperSFS.Prototypes.WriteQueueViewProbe::$variant", '--platform', 'amiga', '--cpu', $cpu,
            '--clr', 'always', '--peephole', 'fixed-point', '--fpu', 'disabled', '--exceptions', 'yolo',
            '--rom-size-optimizations', 'on', '--format', 'hunk', '--runtime', 'freestanding', '--memory', 'none',
            '--exports', 'none', '--symbols', 'off', '--output', $output)
        foreach ($name in $references) { $arguments += @('--managed-assembly', "$OutputDirectory/inputs/$name") }
        dotnet @arguments *> "$output.log"
        $exitCode = $LASTEXITCODE
        if ($variant -eq $baselineName -and $exitCode -ne 0) { Get-Content "$output.log"; throw "Raw baseline failed for $cpu." }
        $compiled = $exitCode -eq 0 -and (Test-Path -LiteralPath $output) -and (Test-Path -LiteralPath "$output.map")
        $hash = if ($compiled) { (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash } else { $null }
        $map = if ($compiled) { [IO.File]::ReadAllText("$output.map") } else { '' }
        $codeBytes = if ($map -match '(?m)^METRICS .*?\bcode-bytes=(\d+)') { [int]$Matches[1] } else { $null }
        $retained = if ($map -match '(?m)^MACHINE .*?\bretained-methods=(\d+)') { [int]$Matches[1] } else { $null }
        if ($variant -eq $baselineName) { $baselines[$baselineName] = @{ hash = $hash; codeBytes = $codeBytes } }
        $baseline = $baselines[$baselineName]
        [ordered]@{
            cpu = $cpu; variant = $variant; compiled = $compiled; exitCode = $exitCode
            sha256 = $hash; identicalToRaw = $compiled -and $hash -eq $baseline.hash
            baseline = $baselineName; codeBytes = $codeBytes; retainedMethods = $retained
            hasFrameStorage = $map.Contains('FRAME-SLOT')
            noExtraCodeOrStorage = $compiled -and $null -ne $codeBytes -and
                $codeBytes -le $baseline.codeBytes -and $retained -eq 1 -and !$map.Contains('FRAME-SLOT')
            diagnostic = $(if (!$compiled) { [IO.File]::ReadAllText("$output.log").Trim() } else { $null })
        }
    }
}
$report = [ordered]@{
    compilerSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $CompilerCli) 'CopperSharp.Compiler.dll') -Algorithm SHA256).Hash
    inputs = @($references | ForEach-Object { @{ name = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $InputDirectory $_) -Algorithm SHA256).Hash } })
    sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object { @{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    results = @($results)
    allViewsEquivalent = @($results | Where-Object { !$_.identicalToRaw }).Count -eq 0
    productionViewsHaveNoExtraCodeOrStorage = @($results | Where-Object {
        $_.variant.StartsWith('Borrowed') -and !$_.noExtraCodeOrStorage
    }).Count -eq 0
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$OutputDirectory/codegen-report.json"
$results | ForEach-Object { [pscustomobject]$_ } | Format-Table cpu, variant, compiled, codeBytes, retainedMethods, hasFrameStorage, noExtraCodeOrStorage
if ($RequireEquivalent -and !$report.allViewsEquivalent) { throw 'Typed views failed the unchanged-code gate; see codegen-report.json.' }
if ($RequireNoViewOverhead -and !$report.productionViewsHaveNoExtraCodeOrStorage) {
    throw 'Production views failed the code-size and storage gate; see codegen-report.json.'
}
