[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$fixtureRoot = $PSScriptRoot
$manifestPath = Join-Path $fixtureRoot 'manifest.json'
$offlineRoot = Join-Path $fixtureRoot 'offline'
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Set-Bytes {
    param(
        [Parameter(Mandatory)][byte[]]$Buffer,
        [Parameter(Mandatory)][int]$Offset,
        [Parameter(Mandatory)][byte[]]$Bytes)

    [Buffer]::BlockCopy($Bytes, 0, $Buffer, $Offset, $Bytes.Length)
}

function Set-UInt16([byte[]]$Buffer, [int]$Offset, [UInt16]$Value) {
    Set-Bytes $Buffer $Offset ([BitConverter]::GetBytes($Value))
}

function Set-UInt32([byte[]]$Buffer, [int]$Offset, [UInt32]$Value) {
    Set-Bytes $Buffer $Offset ([BitConverter]::GetBytes($Value))
}

function Set-Int32([byte[]]$Buffer, [int]$Offset, [Int32]$Value) {
    Set-Bytes $Buffer $Offset ([BitConverter]::GetBytes($Value))
}

function Set-UInt64([byte[]]$Buffer, [int]$Offset, [UInt64]$Value) {
    Set-Bytes $Buffer $Offset ([BitConverter]::GetBytes($Value))
}

function Set-Int64([byte[]]$Buffer, [int]$Offset, [Int64]$Value) {
    Set-Bytes $Buffer $Offset ([BitConverter]::GetBytes($Value))
}

function Get-HexDigest([byte[]]$Bytes) {
    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function New-CanonicalRegion([string]$State) {
    $region = [byte[]]::new(1368)

    Set-UInt32 $region 0 0x32534d53
    Set-UInt16 $region 4 2
    Set-UInt16 $region 6 0
    Set-Int32 $region 8 512
    Set-Int32 $region 12 2
    Set-UInt64 $region 16 7
    Set-UInt64 $region 24 0
    Set-Int64 $region 32 $region.LongLength
    Set-UInt64 $region 40 0x0102030405060708
    Set-Int64 $region 48 $(if ($State -eq 'corrupt') { 3 } else { 2 })
    Set-Int64 $region 56 1
    Set-Int32 $region 64 1
    Set-Int32 $region 68 1
    Set-Int32 $region 72 1
    Set-Int32 $region 76 1
    Set-Int32 $region 80 0
    Set-Int32 $region 84 1
    Set-Int32 $region 88 1
    Set-Int32 $region 92 27
    Set-Int64 $region 96 512
    Set-Int64 $region 104 64
    Set-Int32 $region 112 64
    Set-Int32 $region 116 32
    Set-Int32 $region 120 4
    Set-Int32 $region 124 128
    Set-Int64 $region 128 576
    Set-Int64 $region 136 512
    Set-Int64 $region 144 1088
    Set-Int64 $region 152 8
    Set-Int32 $region 160 8
    Set-Int32 $region 164 64
    Set-Int64 $region 168 1152
    Set-Int64 $region 176 64
    Set-Int32 $region 184 128
    Set-Int32 $region 188 8
    Set-Int64 $region 192 1216
    Set-Int64 $region 200 128
    Set-Int64 $region 208 1344
    Set-Int64 $region 216 8
    Set-Int32 $region 224 8
    Set-Int32 $region 228 8
    Set-Int64 $region 232 1352
    Set-Int64 $region 240 8
    Set-Int64 $region 248 1360
    Set-Int64 $region 256 8
    Set-UInt64 $region 264 0
    Set-Int64 $region 272 1

    $participantToken = [UInt64]3
    $participantState = if ($State -eq 'recovering') { 4 } else { 2 }
    $participantControl = [UInt64]$participantState `
        -bor ([UInt64]1 -shl 3) `
        -bor ([UInt64]4242 -shl 31)
    Set-UInt64 $region 512 $participantControl
    Set-Int32 $region 520 2
    Set-Int64 $region 528 987654321
    Set-Int64 $region 536 1
    Set-UInt64 $region 544 0

    $binding = [UInt64]0x0000000080000001
    $publishedControl = [UInt64]11
    $reservedControl = [UInt64]2 -bor ([UInt64]1 -shl 3) -bor ($participantToken -shl 36)
    $leaseControl = [UInt64]2 -bor ([UInt64]1 -shl 3) -bor ($participantToken -shl 36)
    $directoryInsertPrepared = [UInt64]0x0000000020000005
    $directoryInsertCompletePrimary = [UInt64]0x0000000020000035
    $primaryLocation = [UInt64]0x0000000001000001
    $overflowLocation = [UInt64]0x0000000001000002

    switch ($State) {
        'reserved' {
            Set-UInt64 $region 1216 $reservedControl
            Set-UInt64 $region 1224 $binding
            Set-UInt64 $region 1232 $primaryLocation
            Set-UInt64 $region 1240 $directoryInsertPrepared
            Set-UInt64 $region 592 $binding
        }
        'published' {
            Set-UInt64 $region 1216 $publishedControl
            Set-UInt64 $region 1224 $binding
            Set-UInt64 $region 1232 $primaryLocation
            Set-UInt64 $region 1240 $directoryInsertCompletePrimary
            Set-UInt64 $region 592 $binding
        }
        'leased' {
            Set-UInt64 $region 1216 $publishedControl
            Set-UInt64 $region 1224 $binding
            Set-UInt64 $region 1232 $primaryLocation
            Set-UInt64 $region 1240 $directoryInsertCompletePrimary
            Set-UInt64 $region 592 $binding
            Set-UInt64 $region 1152 $leaseControl
            Set-UInt64 $region 1160 $binding
            Set-Int64 $region 1168 2
        }
        'pending-removal' {
            Set-UInt64 $region 1216 12
            Set-UInt64 $region 1224 $binding
            Set-UInt64 $region 1232 $primaryLocation
            Set-UInt64 $region 1240 $directoryInsertCompletePrimary
            Set-UInt64 $region 592 $binding
            Set-UInt64 $region 1152 $leaseControl
            Set-UInt64 $region 1160 $binding
            Set-Int64 $region 1168 2
        }
        'spilled' {
            Set-UInt64 $region 1216 $publishedControl
            Set-UInt64 $region 1224 $binding
            Set-UInt64 $region 1232 $overflowLocation
            Set-UInt64 $region 1240 0x0000000020000055
            Set-UInt64 $region 576 0x0020000000100001
            Set-UInt64 $region 1088 $binding
        }
        'recovering' {
            Set-UInt64 $region 1216 $reservedControl
            Set-UInt64 $region 1224 $binding
            Set-UInt64 $region 1240 $directoryInsertPrepared
        }
        'reclaimed' {
            Set-UInt64 $region 1216 16
        }
    }

    if ($State -in @('reserved', 'published', 'leased', 'pending-removal', 'spilled', 'recovering')) {
        Set-UInt64 $region 1248 ([Convert]::ToUInt64('af63e64c8601fd8a', 16))
        Set-Int32 $region 1256 1
        Set-Int32 $region 1260 0
        Set-Int32 $region 1264 1
        Set-Int32 $region 1268 $(if ($State -eq 'reserved' -or $State -eq 'recovering') { 1 } else { 2 })
        Set-Int64 $region 1272 $(if ($State -eq 'reserved' -or $State -eq 'recovering') { 0 } else { 1 })
        Set-Int64 $region 1280 2
        Set-Int64 $region 1288 1344
        Set-Int64 $region 1296 1352
        Set-Int64 $region 1304 1360
        $region[1344] = 0x6b
        $region[1360] = 0x76
    }

    return $region
}

if (-not [BitConverter]::IsLittleEndian) {
    throw 'SMS2 fixture generation requires a little-endian host.'
}

New-Item -ItemType Directory -Path $offlineRoot -Force | Out-Null
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$states = @(
    'empty',
    'reserved',
    'published',
    'leased',
    'pending-removal',
    'spilled',
    'recovering',
    'reclaimed',
    'corrupt')

foreach ($state in $states) {
    $entry = @($manifest.offline_fixtures | Where-Object state -EQ $state)
    if ($entry.Count -ne 1) {
        throw "Manifest must contain exactly one offline fixture entry for '$state'."
    }

    $region = New-CanonicalRegion $state
    $binaryPath = Join-Path $fixtureRoot $entry[0].binary_path
    [IO.File]::WriteAllBytes($binaryPath, $region)

    $snapshot = [ordered]@{
        offline_only = $true
        state = $state
        protocol = [ordered]@{
            layout_major = 2
            layout_minor = 0
            resource_protocol = 2
            required_features = 7
            optional_features = 0
        }
        fixture = [ordered]@{
            byte_length = $region.LongLength
            store_id_hex = '0102030405060708'
            representative_only = $true
        }
    }
    $snapshotText = (($snapshot | ConvertTo-Json -Depth 10) -replace "`r`n", "`n") + "`n"
    $snapshotBytes = $utf8NoBom.GetBytes($snapshotText)
    $snapshotPath = Join-Path $fixtureRoot $entry[0].snapshot_path
    [IO.File]::WriteAllBytes($snapshotPath, $snapshotBytes)

    $entry[0].byte_length = $region.LongLength
    $entry[0].binary_sha256_hex = Get-HexDigest $region
    $entry[0].snapshot_sha256_hex = Get-HexDigest $snapshotBytes
}

$manifestText = (($manifest | ConvertTo-Json -Depth 100) -replace "`r`n", "`n") + "`n"
[IO.File]::WriteAllText($manifestPath, $manifestText, $utf8NoBom)

Write-Host "Generated $($states.Count) canonical offline SMS2 fixtures."
