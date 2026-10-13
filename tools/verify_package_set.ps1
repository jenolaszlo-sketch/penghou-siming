param([Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][string] $PackageDirectory, [string] $ExpectedCommit)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
$ids = @('Penghou.Siming','Penghou.Siming.Sqlite','Penghou.Siming.Cryptography','Penghou.Siming.Testing','Penghou.Siming.Verify')
if ($Version -notmatch '^1\.0\.0(?:-rc\.[1-9][0-9]*)?$') { throw 'Not a supported graduation version' }
$files = @(Get-ChildItem -LiteralPath $PackageDirectory -File | Where-Object { $_.Extension -in @('.nupkg','.snupkg') })
if ($files.Count -ne 10) { throw 'Require exactly five packages and five symbol archives' }
$records = @()
foreach ($id in $ids) {
    foreach ($extension in @('nupkg','snupkg')) {
        $path = Join-Path $PackageDirectory "$id.$Version.$extension"
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing exact artifact $path" }
        $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($path))
        try {
            $nuspec = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec',[StringComparison]::Ordinal) })
            if ($nuspec.Count -ne 1) { throw 'Require one manifest' }
            $reader = [IO.StreamReader]::new($nuspec[0].Open())
            try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $xml.package.metadata
            if ($metadata.id -ne $id -or $metadata.version -ne $Version) { throw 'Package identity mismatch' }
            if ($ExpectedCommit -and $metadata.repository.commit -ne $ExpectedCommit) { throw 'Package source commit mismatch' }
            if ($extension -eq 'nupkg') {
                $dllPath = if ($id -eq 'Penghou.Siming.Verify') { "tools/net8.0/any/$id.dll" } else { "lib/net8.0/$id.dll" }
                if (-not $archive.GetEntry($dllPath)) { throw "Missing net8.0 payload $dllPath" }
                if ($archive.Entries.FullName -match '^lib/net(9|10)') { throw 'Unexpected new framework' }
            }
            $records += [ordered]@{ Package=$id; Version=$Version; Archive=$extension; SourceCommit=[string]$metadata.repository.commit; SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        } finally { $archive.Dispose() }
    }
}
$records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PackageDirectory 'package-evidence.json')
Write-Output 'SUPPORTED BY THIS PROBE: exact five-package identity/framework/source/hash set'
