param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $GuyabanoSourceDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $PackageDirectory,
    [string] $BaselineVersion = '0.1.0-preview.7',
    [switch] $PublicPackage
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$run = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$cache = Join-Path $run 'packages'
$ids = @('Penghou.Siming','Penghou.Siming.Sqlite','Penghou.Siming.Cryptography','Penghou.Siming.Testing')
$consumerCommit = 'df845087a663b51929c081f7e83b9852598bc995'
if ((git -C $GuyabanoSourceDirectory rev-parse HEAD).Trim() -ne $consumerCommit) { throw 'Wrong Guyabano source revision' }
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
$archive = Join-Path $run 'guyabano.tar'
git -C $GuyabanoSourceDirectory archive --format=tar --output=$archive $consumerCommit src/Guyabano.Session src/Guyabano.Session.Sqlite tests/Guyabano.Session.Tests
if ($LASTEXITCODE -ne 0) { throw 'Guyabano source snapshot failed' }
$records = [Collections.Generic.List[object]]::new()
$filter = 'FullyQualifiedName~SimingSessionEventStoreTests|FullyQualifiedName~SessionCheckpointAnchoringTests'
foreach ($label in @('baseline','candidate')) {
    $selectedVersion = if ($label -eq 'baseline') { $BaselineVersion } else { $Version }
    $folder = Join-Path $run $label
    New-Item -ItemType Directory -Path $folder | Out-Null
    tar -xf $archive -C $folder
    if ($LASTEXITCODE -ne 0) { throw 'Guyabano extraction failed' }
    '<Project />' | Set-Content (Join-Path $folder 'Directory.Build.props')
    '<Project />' | Set-Content (Join-Path $folder 'Directory.Build.targets')
    $config = Join-Path $folder 'NuGet.Config'
    Config $config ($label -eq 'candidate')
    $adapter = Join-Path $folder 'src/Guyabano.Session.Sqlite/Guyabano.Session.Sqlite.csproj'
    (Get-Content -LiteralPath $adapter -Raw).Replace('Version="0.1.0-preview.7"', "Version=`"[$selectedVersion]`"") | Set-Content -LiteralPath $adapter
    $project = Join-Path $folder 'tests/Guyabano.Session.Tests/Guyabano.Session.Tests.csproj'
    $references = ($ids | ForEach-Object { "<PackageReference Include=`"$_`" Version=`"[$selectedVersion]`" />" }) -join ''
    (Get-Content -LiteralPath $project -Raw).Replace('</Project>', "<ItemGroup>$references</ItemGroup></Project>") | Set-Content -LiteralPath $project
    Checked @('restore',$project,'--configfile',$config,"-p:RestorePackagesPath=$cache",'--no-cache') "guyabano-$label-restore.log"
    $assets = Get-Content (Join-Path $folder 'tests/Guyabano.Session.Tests/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($id in $ids) { if ("$id/$selectedVersion" -notin $assets.libraries.PSObject.Properties.Name) { throw "Wrong Guyabano package $id" } }
    Checked @('test',$project,'-c','Release','--no-restore','--filter',$filter,'--logger','trx',"--results-directory=$run/guyabano-$label") "guyabano-$label-tests.log"
    $hongxian = Join-Path $folder 'hongxian'
    New-Item -ItemType Directory -Path $hongxian | Out-Null
    "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><PackageReference Include=`"Penghou.Hongxian.Sqlite`" Version=`"[0.1.0-preview.5]`" />$references</ItemGroup></Project>" | Set-Content (Join-Path $hongxian 'Consumer.csproj')
    Copy-Item -LiteralPath (Join-Path $repo 'qualification/HongxianConsumer.cs') -Destination (Join-Path $hongxian 'Program.cs')
    $hongxianProject = Join-Path $hongxian 'Consumer.csproj'
    Checked @('restore',$hongxianProject,'--configfile',$config,"-p:RestorePackagesPath=$cache",'--no-cache') "hongxian-$label-restore.log"
    Checked @('build',$hongxianProject,'-c','Release','--no-restore') "hongxian-$label-build.log"
    Checked @((Join-Path $hongxian 'bin/Release/net10.0/Consumer.dll'),(Join-Path $run "hongxian-$label-data")) "hongxian-$label-behavior.log"
    $records.Add([ordered]@{ Probe='actual-consumer-source'; CompiledAgainst=$selectedVersion; GuyabanoCommit=$consumerCommit; GuyabanoFilter=$filter; HongxianVersion='0.1.0-preview.5'; Result='SUPPORTED BY THIS PROBE' })
}
foreach ($relative in @('tests/Guyabano.Session.Tests/bin/Release/net10.0','hongxian/bin/Release/net10.0')) {
    $baseline = Join-Path $run "baseline/$relative"
    $candidate = Join-Path $run "candidate/$relative"
    $consumerNames = if ($relative.StartsWith('tests/')) { @('Guyabano.Session.Tests.dll','Guyabano.Session.dll','Guyabano.Session.Sqlite.dll') } else { @('Consumer.dll','Penghou.Hongxian.dll','Penghou.Hongxian.Sqlite.dll') }
    $before = @{}
    foreach ($name in $consumerNames) { $before[$name] = (Get-FileHash (Join-Path $baseline $name) -Algorithm SHA256).Hash }
    foreach ($id in $ids) { Copy-Item -LiteralPath (Join-Path $candidate "$id.dll") -Destination (Join-Path $baseline "$id.dll") -Force }
    if ($relative.StartsWith('tests/')) {
        Checked @('vstest',(Join-Path $baseline 'Guyabano.Session.Tests.dll'),"/TestCaseFilter:$filter",'/logger:trx',"/ResultsDirectory:$run/guyabano-unchanged") 'guyabano-unchanged-binary.log'
    } else {
        Checked @((Join-Path $baseline 'Consumer.dll'),(Join-Path $run 'hongxian-unchanged-data')) 'hongxian-unchanged-binary.log'
    }
    foreach ($name in $consumerNames) { if ((Get-FileHash (Join-Path $baseline $name) -Algorithm SHA256).Hash -ne $before[$name]) { throw "Changed consumer binary: $name" } }
    $records.Add([ordered]@{ Probe='unchanged-consumer-binary'; CompiledAgainst=$BaselineVersion; RunsAgainst=$Version; ConsumerHashes=$before; Result='SUPPORTED BY THIS PROBE' })
}
[ordered]@{
    Repository='https://github.com/jenolaszlo-sketch/penghou-siming'; Commit=(git -C $repo rev-parse HEAD); Branch=$(if ($env:GITHUB_REF) { $env:GITHUB_REF } else { git -C $repo branch --show-current });
    WorkingTreeStatus=@(git -C $repo status --porcelain); QualifiedAt=[DateTimeOffset]::UtcNow.ToString('O'); OS=[Runtime.InteropServices.RuntimeInformation]::OSDescription;
    Framework='net10.0'; Version=$Version; BaselineVersion=$BaselineVersion; PublicPackage=[bool]$PublicPackage; Results=$records.ToArray();
    Limits='Only original Guyabano session/checkpoint tests and published Hongxian session-store path. No full application or downstream graduation claim. Source modification is limited to isolated package references; original checkouts untouched.'
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'qualification.json')
Write-Output (Join-Path $run 'qualification.json')
