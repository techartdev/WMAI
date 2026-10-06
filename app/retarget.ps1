# Retargets a desktop-.NET-2.0-compiled assembly to .NET Compact Framework 3.5
# by rewriting its AssemblyRef rows in place: version -> 3.5.0.0 and public key
# token -> the NETCF token. NETCF only unifies some desktop references itself
# (mscorlib/System), not System.Windows.Forms or System.Drawing.
# Requires PowerShell 7 (System.Reflection.Metadata ships with .NET).
param([Parameter(Mandatory)] [string] $Path)

$ErrorActionPreference = 'Stop'
$Path = (Resolve-Path $Path).Path
$Retarget = @('mscorlib', 'System', 'System.Drawing', 'System.Windows.Forms', 'System.Xml', 'System.Data')
$CfToken = [byte[]](0x96, 0x9d, 0xb8, 0x05, 0x3d, 0x33, 0x22, 0xac)

$bytes = [IO.File]::ReadAllBytes($Path)
$stream = [IO.MemoryStream]::new($bytes)
$pe = [Reflection.PortableExecutable.PEReader]::new($stream)
$md = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
$mdStart = $pe.PEHeaders.MetadataStartOffset
$table = [Reflection.Metadata.Ecma335.TableIndex]::AssemblyRef
$tableOffset = $mdStart + [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableMetadataOffset($md, $table)
$rowSize = [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableRowSize($md, $table)
$blobHeap = $mdStart + [Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetHeapMetadataOffset($md, [Reflection.Metadata.Ecma335.HeapIndex]::Blob)

$patches = @()
foreach ($h in $md.AssemblyReferences) {
    $ref = $md.GetAssemblyReference($h)
    $name = $md.GetString($ref.Name)
    if ($Retarget -notcontains $name) { continue }
    $row = [Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber([Reflection.Metadata.AssemblyReferenceHandle]$h)
    $at = $tableOffset + ($row - 1) * $rowSize
    # Row layout: Major, Minor, Build, Revision (u16 each), Flags (u32), PublicKeyOrToken (blob idx), ...
    $patches += , @($at, $ref.PublicKeyOrToken, $name)
}
$pe.Dispose()

$tokens = @{}
foreach ($p in $patches) {
    $at = $p[0]
    [BitConverter]::GetBytes([uint16]3).CopyTo($bytes, $at)
    [BitConverter]::GetBytes([uint16]5).CopyTo($bytes, $at + 2)
    [BitConverter]::GetBytes([uint16]0).CopyTo($bytes, $at + 4)
    [BitConverter]::GetBytes([uint16]0).CopyTo($bytes, $at + 6)
    # Token blobs: 1-byte length (8) followed by the token; identical tokens share one blob.
    $blobAt = $blobHeap + [Reflection.Metadata.Ecma335.MetadataTokens]::GetHeapOffset([Reflection.Metadata.BlobHandle]$p[1])
    if ($bytes[$blobAt] -ne 8) { throw "Unexpected public key blob for $($p[2])" }
    $tokens[$blobAt] = $true
    Write-Host "  $($p[2]) -> 3.5.0.0, 969db8053d3322ac"
}
foreach ($blobAt in $tokens.Keys) { $CfToken.CopyTo($bytes, $blobAt + 1) }
[IO.File]::WriteAllBytes($Path, $bytes)
