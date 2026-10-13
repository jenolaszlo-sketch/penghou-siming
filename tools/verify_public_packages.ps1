param([Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][string] $PackageDirectory, [Parameter(Mandatory)][string] $OutputDirectory, [int] $TimeoutSeconds=3600)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$ids = @('Penghou.Siming','Penghou.Siming.Sqlite','Penghou.Siming.Cryptography','Penghou.Siming.Testing','Penghou.Siming.Verify')
function EntryHashes([string] $Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
    $entries = @{}
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -eq '.signature.p7s' -or $entry.FullName.EndsWith('/')) { continue }
            $stream = $entry.Open()
            try { $entries[$entry.FullName] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    return $entries
}
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$records = @()
foreach ($id in $ids) {
    $lower = $id.ToLowerInvariant()
    $download = Join-Path $OutputDirectory "$id.$Version.nupkg"
    while ($true) {
        try { Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$lower/$Version/$lower.$Version.nupkg" -OutFile $download; break }
        catch { if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }; Start-Sleep -Seconds 20 }
    }
    $expected = EntryHashes (Join-Path $PackageDirectory "$id.$Version.nupkg")
    $actual = EntryHashes $download
    if ($expected.Count -ne $actual.Count) { throw 'Published entry set differs from qualified artifact' }
    foreach ($name in $expected.Keys) { if (-not $actual.ContainsKey($name) -or $expected[$name] -ne $actual[$name]) { throw "Published bytes differ: $id/$name" } }
    $records += [ordered]@{ Package=$id; Version=$Version; PublicSHA256=(Get-FileHash $download -Algorithm SHA256).Hash; QualifiedSHA256=(Get-FileHash (Join-Path $PackageDirectory "$id.$Version.nupkg") -Algorithm SHA256).Hash; ExcludedEntry='.signature.p7s only'; Result='SUPPORTED BY THIS PROBE: public package payload identical to qualified artifact' }
}
$records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'public-package-evidence.json')
