param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $PackageDirectory,
    [string] $BaselineVersion = '0.1.0-preview.7',
    [Parameter(Mandatory)][string] $MarangSourceDirectory,
    [switch] $PublicPackage,
    [switch] $SkipContractTests
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$run = Join-Path $out ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$ids = @('Penghou.Siming','Penghou.Siming.Sqlite','Penghou.Siming.Cryptography','Penghou.Siming.Testing')
$cache = Join-Path $run 'packages'
$marangSource = Join-Path $MarangSourceDirectory 'src/Marang.Mcp/MarangDelegationFingerprint.cs'
if ((Get-FileHash -LiteralPath $marangSource -Algorithm SHA256).Hash -ne '21FC4925F98319B83B62B5E445BF44EEFC412219224D678445BC4C2CA3E7F7D8') { throw 'Unexpected Marang consumer source' }
function Checked([string[]] $Arguments, [string] $Log) {
    $result = & dotnet @Arguments 2>&1
    $code = $LASTEXITCODE
    $result | Set-Content -LiteralPath (Join-Path $run $Log)
    if ($code -ne 0) { throw "$Log failed: $($result -join [Environment]::NewLine)" }
}
function Config([string] $Path, [bool] $Candidate) {
    $local = ''
    $mapping = ''
    if ($Candidate -and -not $PublicPackage) {
        $feed = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($PackageDirectory))
        $local = "<add key=`"artifact`" value=`"$feed`" />"
        $mapping = '<packageSourceMapping><packageSource key="artifact"><package pattern="Penghou.Siming*" /></packageSource><packageSource key="nuget"><package pattern="*" /></packageSource></packageSourceMapping>'
    }
    $escapedCache = [Security.SecurityElement]::Escape($cache)
    "<configuration><packageSources><clear />$local<add key=`"nuget`" value=`"https://api.nuget.org/v3/index.json`" /></packageSources>$mapping<config><add key=`"globalPackagesFolder`" value=`"$escapedCache`" /></config><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>" | Set-Content -LiteralPath $Path
}
$baselineConfig = Join-Path $run 'baseline.config'
$candidateConfig = Join-Path $run 'candidate.config'
Config $baselineConfig $false
Config $candidateConfig $true
$results = [Collections.Generic.List[object]]::new()
foreach ($label in @('baseline','candidate')) {
    $selectedVersion = if ($label -eq 'baseline') { $BaselineVersion } else { $Version }
    $projectRoot = Join-Path $run $label
    New-Item -ItemType Directory -Path $projectRoot | Out-Null
    '<Project />' | Set-Content (Join-Path $projectRoot 'Directory.Build.props')
    '<Project />' | Set-Content (Join-Path $projectRoot 'Directory.Build.targets')
    $references = ($ids | ForEach-Object { "<PackageReference Include=`"$_`" Version=`"[$selectedVersion]`" />" }) -join ''
    "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>$references</ItemGroup></Project>" | Set-Content (Join-Path $projectRoot 'Consumer.csproj')
    Copy-Item -LiteralPath (Join-Path $repo 'qualification/Consumer.cs') -Destination (Join-Path $projectRoot 'Program.cs')
    Copy-Item -LiteralPath $marangSource -Destination (Join-Path $projectRoot 'MarangDelegationFingerprint.cs')
    $project = Join-Path $projectRoot 'Consumer.csproj'
    $configPath = if ($label -eq 'baseline') { $baselineConfig } else { $candidateConfig }
    Checked @('restore',$project,'--configfile',$configPath,"-p:RestorePackagesPath=$cache",'--no-cache') "$label-restore.log"
    $assets = Get-Content (Join-Path $projectRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($id in $ids) { if ("$id/$selectedVersion" -notin $assets.libraries.PSObject.Properties.Name) { throw "Wrong resolved package $id" } }
    if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ne 'package' }).Count) { throw 'Consumer has a project dependency' }
    Checked @('build',$project,'-c','Release','--no-restore') "$label-build.log"
    Checked @((Join-Path $projectRoot 'bin/Release/net8.0/Consumer.dll'),(Join-Path $run "$label-data")) "$label-behavior.log"
    $results.Add([ordered]@{ Probe='compiled-package-behavior'; CompiledAgainst=$selectedVersion; Result='SUPPORTED BY THIS PROBE' })
}
$baselineOutput = Join-Path $run 'baseline/bin/Release/net8.0'
$candidateOutput = Join-Path $run 'candidate/bin/Release/net8.0'
$consumer = Join-Path $baselineOutput 'Consumer.dll'
$before = (Get-FileHash -LiteralPath $consumer -Algorithm SHA256).Hash
foreach ($id in $ids) { Copy-Item -LiteralPath (Join-Path $candidateOutput "$id.dll") -Destination (Join-Path $baselineOutput "$id.dll") -Force }
Checked @($consumer,(Join-Path $run 'baseline-data')) 'unchanged-binary-behavior.log'
if ((Get-FileHash -LiteralPath $consumer -Algorithm SHA256).Hash -ne $before) { throw 'Consumer binary changed' }
$results.Add([ordered]@{ Probe='unchanged-binary-and-existing-database'; CompiledAgainst=$BaselineVersion; RunsAgainst=$Version; ConsumerSHA256=$before; Result='SUPPORTED BY THIS PROBE' })

if (-not $SkipContractTests) {
    Checked @('build',(Join-Path $repo 'Penghou.Siming.slnx'),'-c','Release') 'contract-harness-build.log'
    $suiteDirs = @('Penghou.Siming.Tests','Penghou.Siming.Sqlite.Tests') | ForEach-Object { Join-Path $repo "tests/$_/bin/Release/net8.0" }
    foreach ($directory in $suiteDirs) {
        foreach ($id in $ids) { Copy-Item -LiteralPath (Join-Path $candidateOutput "$id.dll") -Destination (Join-Path $directory "$id.dll") -Force }
    }
    # TestHarness binaries are compiled once; every product library is the exact package payload.
    $toolRoot = Join-Path $run 'tool'
    Checked @('tool','install','Penghou.Siming.Verify','--version',$Version,'--tool-path',$toolRoot,'--configfile',$candidateConfig,'--no-cache') 'tool-install.log'
    $toolLibrary = Get-ChildItem -LiteralPath $toolRoot -Recurse -Force -Filter 'Penghou.Siming.Verify.dll' | Select-Object -First 1
    if (-not $toolLibrary) { throw 'Verifier payload absent' }
    Copy-Item -LiteralPath $toolLibrary.FullName -Destination (Join-Path $suiteDirs[1] 'Penghou.Siming.Verify.dll') -Force
    Checked @('vstest',(Join-Path $suiteDirs[0] 'Penghou.Siming.Tests.dll'),(Join-Path $suiteDirs[1] 'Penghou.Siming.Sqlite.Tests.dll'),"/ResultsDirectory:$run",'/logger:trx') 'exact-package-contract-tests.log'
    Checked @($toolLibrary.FullName,(Join-Path $run 'candidate-data/ledger.db')) 'packaged-verifier.log'
    $results.Add([ordered]@{ Probe='exact-packaged-libraries-contract-suite-and-verifier'; Result='SUPPORTED BY THIS PROBE' })
}
$artifactRecords = @()
foreach ($id in @($ids + 'Penghou.Siming.Verify')) {
    $package = if ($PublicPackage) { Join-Path $cache "$($id.ToLowerInvariant())/$Version/$($id.ToLowerInvariant()).$Version.nupkg" } else { Join-Path $PackageDirectory "$id.$Version.nupkg" }
    if (Test-Path -LiteralPath $package) { $artifactRecords += [ordered]@{ Package=$id; Version=$Version; SHA256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash } }
}
[ordered]@{
    Repository='https://github.com/jenolaszlo-sketch/penghou-siming'; Branch=(git -C $repo branch --show-current); Commit=(git -C $repo rev-parse HEAD);
    WorkingTreeStatus=@(git -C $repo status --porcelain); QualifiedAt=[DateTimeOffset]::UtcNow.ToString('O'); OS=[Runtime.InteropServices.RuntimeInformation]::OSDescription;
    Framework='net8.0'; PackageVersion=$Version; BaselineVersion=$BaselineVersion; PublicPackage=[bool]$PublicPackage; Artifacts=$artifactRecords; Results=$results.ToArray();
    MarangSourceCommit='bca73774386e3ae2a455a00db3ff637ea5c0db6e'; MarangSourceSHA256=(Get-FileHash -LiteralPath $marangSource -Algorithm SHA256).Hash;
    Commands='restore fresh package cache; build isolated package consumer; run behavior; replace product DLLs without rebuilding consumer; reopen old database; exact package contract suite; install/run verifier';
    Limits='Standalone foundation contracts and existing consumer-shaped paths. Real Guyabano/Hongxian/Marang adapters are separately qualified. No full-application or new framework certification.'
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'qualification.json')
Write-Output (Join-Path $run 'qualification.json')
