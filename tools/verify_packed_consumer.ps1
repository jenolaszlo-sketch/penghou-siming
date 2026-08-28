param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,
    [Parameter(Mandatory = $true)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$packageSource = (Resolve-Path $PackageDirectory).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "siming-consumer-$([Guid]::NewGuid().ToString('N'))"
$projectPath = Join-Path $temporaryRoot 'PackedConsumer.csproj'
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $escapedSource = [Security.SecurityElement]::Escape($packageSource)
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NuGetAudit>true</NuGetAudit>
    <RestoreSources>$escapedSource;https://api.nuget.org/v3/index.json</RestoreSources>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Penghou.Siming" Version="$Version" />
    <PackageReference Include="Penghou.Siming.Sqlite" Version="$Version" />
    <PackageReference Include="Penghou.Siming.Cryptography" Version="$Version" />
    <PackageReference Include="Penghou.Siming.Testing" Version="$Version" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path $projectPath -Encoding utf8

    dotnet restore $projectPath --force --no-cache
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer restore failed." }
    dotnet build $projectPath --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer build failed." }
    dotnet list $projectPath package --vulnerable --include-transitive
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer vulnerability audit failed." }
}
finally {
    if (Test-Path $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
