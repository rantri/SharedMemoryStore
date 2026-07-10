[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$fixtureRoot = $PSScriptRoot
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)

function ConvertFrom-Hex {
    param([Parameter(Mandatory)][string]$Hex)

    if (($Hex.Length % 2) -ne 0) {
        throw "Hex input must contain an even number of characters: '$Hex'."
    }

    $result = [byte[]]::new($Hex.Length / 2)
    for ($index = 0; $index -lt $result.Length; $index++) {
        $result[$index] = [Convert]::ToByte($Hex.Substring($index * 2, 2), 16)
    }

    return ,$result
}

function ConvertTo-Hex {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)

    return [Convert]::ToHexString($Bytes).ToLowerInvariant()
}

function Set-Bytes {
    param(
        [Parameter(Mandatory)][byte[]]$Buffer,
        [Parameter(Mandatory)][int]$Offset,
        [Parameter(Mandatory)][byte[]]$Value
    )

    [Buffer]::BlockCopy($Value, 0, $Buffer, $Offset, $Value.Length)
}

function Set-Int32 {
    param([byte[]]$Buffer, [int]$Offset, [int]$Value)

    $bytes = [BitConverter]::GetBytes($Value)
    if (-not [BitConverter]::IsLittleEndian) {
        [Array]::Reverse($bytes)
    }

    Set-Bytes $Buffer $Offset $bytes
}

function Set-Int64 {
    param([byte[]]$Buffer, [int]$Offset, [long]$Value)

    $bytes = [BitConverter]::GetBytes($Value)
    if (-not [BitConverter]::IsLittleEndian) {
        [Array]::Reverse($bytes)
    }

    Set-Bytes $Buffer $Offset $bytes
}

function Set-UInt64 {
    param([byte[]]$Buffer, [int]$Offset, [UInt64]$Value)

    $bytes = [BitConverter]::GetBytes($Value)
    if (-not [BitConverter]::IsLittleEndian) {
        [Array]::Reverse($bytes)
    }

    Set-Bytes $Buffer $Offset $bytes
}

function Get-Int32 {
    param([byte[]]$Buffer, [int]$Offset)

    return [BitConverter]::ToInt32($Buffer, $Offset)
}

function Get-Int64 {
    param([byte[]]$Buffer, [int]$Offset)

    return [BitConverter]::ToInt64($Buffer, $Offset)
}

function Get-UInt64 {
    param([byte[]]$Buffer, [int]$Offset)

    return [BitConverter]::ToUInt64($Buffer, $Offset)
}

function Get-Bytes {
    param([byte[]]$Buffer, [int]$Offset, [int]$Length)

    $result = [byte[]]::new($Length)
    if ($Length -gt 0) {
        [Buffer]::BlockCopy($Buffer, $Offset, $result, 0, $Length)
    }

    return ,$result
}

function New-CanonicalRegion {
    $region = [byte[]]::new(1016)

    # Store header: layout v1.2, the alignment vector from manifest.json.
    Set-Int32 $region 0 0x31534d53
    Set-Int32 $region 4 1
    Set-Int32 $region 8 2
    Set-Int32 $region 12 160
    Set-Int64 $region 16 1016
    Set-Int32 $region 24 3
    Set-Int32 $region 28 4
    Set-Int32 $region 32 9
    Set-Int32 $region 36 5
    Set-Int32 $region 40 17
    Set-Int32 $region 44 8
    Set-Int32 $region 48 48
    Set-Int64 $region 56 160
    Set-Int64 $region 64 384
    Set-Int64 $region 72 544
    Set-Int64 $region 80 160
    Set-Int64 $region 88 704
    Set-Int64 $region 96 216
    Set-Int64 $region 104 920
    Set-Int64 $region 112 24
    Set-Int64 $region 120 944
    Set-Int64 $region 128 72
    Set-Int64 $region 136 0x0102030405060708
    Set-Int32 $region 144 1
    Set-Int32 $region 148 0
    Set-Int64 $region 152 0

    # Free lease records retain their canonical IDs and use -1 as the slot sentinel.
    for ($recordIndex = 0; $recordIndex -lt 4; $recordIndex++) {
        $recordOffset = 544 + ($recordIndex * 40)
        Set-Int32 $region ($recordOffset + 4) $recordIndex
        Set-Int32 $region ($recordOffset + 8) -1
    }

    # Free slots start at lifecycle identity (generation 1, reuse epoch 0).
    for ($slotIndex = 0; $slotIndex -lt 3; $slotIndex++) {
        $slotOffset = 704 + ($slotIndex * 72)
        Set-Int32 $region ($slotOffset + 4) 1
        Set-Int64 $region ($slotOffset + 48) (920 + ($slotIndex * 8))
        Set-Int64 $region ($slotOffset + 56) (944 + ($slotIndex * 24))
    }

    return ,$region
}

function Set-IndexEntry {
    param(
        [byte[]]$Region,
        [int]$EntryIndex,
        [int]$State,
        [UInt64]$KeyHash,
        [int]$SlotIndex,
        [int]$Generation,
        [long]$ReuseEpoch,
        [byte[]]$Key
    )

    $entryOffset = 160 + ($EntryIndex * 48)
    Set-Int32 $Region $entryOffset $State
    Set-Int32 $Region ($entryOffset + 4) $Key.Length
    Set-UInt64 $Region ($entryOffset + 8) $KeyHash
    Set-Int32 $Region ($entryOffset + 16) $SlotIndex
    Set-Int32 $Region ($entryOffset + 20) $Generation
    Set-Int64 $Region ($entryOffset + 24) $ReuseEpoch
    Set-Bytes $Region ($entryOffset + 32) $Key
}

function Set-Slot {
    param(
        [byte[]]$Region,
        [int]$SlotIndex,
        [int]$State,
        [int]$Generation,
        [long]$ReuseEpoch,
        [int]$UsageCount,
        [byte[]]$Key,
        [byte[]]$Descriptor,
        [byte[]]$Payload,
        [int]$PublisherProcessId,
        [int]$Reserved,
        [UInt64]$KeyHash,
        [long]$CommittedSequence
    )

    $slotOffset = 704 + ($SlotIndex * 72)
    $descriptorOffset = 920 + ($SlotIndex * 8)
    $payloadOffset = 944 + ($SlotIndex * 24)
    Set-Int32 $Region $slotOffset $State
    Set-Int32 $Region ($slotOffset + 4) $Generation
    Set-Int64 $Region ($slotOffset + 8) $ReuseEpoch
    Set-Int32 $Region ($slotOffset + 16) $UsageCount
    Set-Int32 $Region ($slotOffset + 20) $Key.Length
    Set-Int32 $Region ($slotOffset + 24) $Descriptor.Length
    Set-Int32 $Region ($slotOffset + 28) $Payload.Length
    Set-Int32 $Region ($slotOffset + 32) $PublisherProcessId
    Set-Int32 $Region ($slotOffset + 36) $Reserved
    Set-UInt64 $Region ($slotOffset + 40) $KeyHash
    Set-Int64 $Region ($slotOffset + 48) $descriptorOffset
    Set-Int64 $Region ($slotOffset + 56) $payloadOffset
    Set-Int64 $Region ($slotOffset + 64) $CommittedSequence
    Set-Bytes $Region $descriptorOffset $Descriptor
    Set-Bytes $Region $payloadOffset $Payload
}

function Set-Lease {
    param(
        [byte[]]$Region,
        [int]$RecordId,
        [int]$SlotIndex,
        [int]$Generation,
        [long]$ReuseEpoch,
        [int]$OwnerProcessId,
        [long]$AcquireSequence
    )

    $recordOffset = 544 + ($RecordId * 40)
    Set-Int32 $Region $recordOffset 1
    Set-Int32 $Region ($recordOffset + 4) $RecordId
    Set-Int32 $Region ($recordOffset + 8) $SlotIndex
    Set-Int32 $Region ($recordOffset + 12) $Generation
    Set-Int64 $Region ($recordOffset + 16) $ReuseEpoch
    Set-Int32 $Region ($recordOffset + 24) $OwnerProcessId
    Set-Int32 $Region ($recordOffset + 28) 0
    Set-Int64 $Region ($recordOffset + 32) $AcquireSequence
}

function Get-IndexStateName([int]$State) {
    return @('Empty', 'Occupied', 'Tombstone')[$State]
}

function Get-SlotStateName([int]$State) {
    return @('Free', 'Publishing', 'Published', 'RemoveRequested', 'Reclaiming')[$State]
}

function Get-LeaseStateName([int]$State) {
    return @('Free', 'Active', 'Released', 'Abandoned')[$State]
}

function Get-NormalizedSnapshot {
    param([string]$FixtureName, [byte[]]$Region)

    $indexEntries = @()
    for ($entryIndex = 0; $entryIndex -lt 8; $entryIndex++) {
        $offset = 160 + ($entryIndex * 48)
        $state = Get-Int32 $Region $offset
        if ($state -eq 0) {
            continue
        }

        $keyLength = Get-Int32 $Region ($offset + 4)
        $indexEntries += [ordered]@{
            entry_index = $entryIndex
            state = $state
            state_name = Get-IndexStateName $state
            key_hex = ConvertTo-Hex (Get-Bytes $Region ($offset + 32) $keyLength)
            key_hash_hex = '{0:x16}' -f (Get-UInt64 $Region ($offset + 8))
            slot_index = Get-Int32 $Region ($offset + 16)
            slot_generation = Get-Int32 $Region ($offset + 20)
            slot_reuse_epoch = Get-Int64 $Region ($offset + 24)
        }
    }

    $leaseRecords = @()
    for ($recordIndex = 0; $recordIndex -lt 4; $recordIndex++) {
        $offset = 544 + ($recordIndex * 40)
        $state = Get-Int32 $Region $offset
        if ($state -eq 0) {
            continue
        }

        $leaseRecords += [ordered]@{
            record_id = Get-Int32 $Region ($offset + 4)
            state = $state
            state_name = Get-LeaseStateName $state
            slot_index = Get-Int32 $Region ($offset + 8)
            slot_generation = Get-Int32 $Region ($offset + 12)
            slot_reuse_epoch = Get-Int64 $Region ($offset + 16)
            owner_process_id = Get-Int32 $Region ($offset + 24)
            acquire_sequence = Get-Int64 $Region ($offset + 32)
        }
    }

    $slots = @()
    for ($slotIndex = 0; $slotIndex -lt 3; $slotIndex++) {
        $offset = 704 + ($slotIndex * 72)
        $state = Get-Int32 $Region $offset
        $descriptorLength = Get-Int32 $Region ($offset + 24)
        $payloadLength = Get-Int32 $Region ($offset + 28)
        $descriptorOffset = Get-Int64 $Region ($offset + 48)
        $payloadOffset = Get-Int64 $Region ($offset + 56)
        $slots += [ordered]@{
            slot_index = $slotIndex
            state = $state
            state_name = Get-SlotStateName $state
            generation = Get-Int32 $Region ($offset + 4)
            reuse_epoch = Get-Int64 $Region ($offset + 8)
            usage_count = Get-Int32 $Region ($offset + 16)
            key_length = Get-Int32 $Region ($offset + 20)
            descriptor_hex = ConvertTo-Hex (Get-Bytes $Region $descriptorOffset $descriptorLength)
            payload_hex = ConvertTo-Hex (Get-Bytes $Region $payloadOffset $payloadLength)
            publisher_process_id = Get-Int32 $Region ($offset + 32)
            reservation_bytes_written = Get-Int32 $Region ($offset + 36)
            key_hash_hex = '{0:x16}' -f (Get-UInt64 $Region ($offset + 40))
            descriptor_offset = $descriptorOffset
            payload_offset = $payloadOffset
            committed_sequence = Get-Int64 $Region ($offset + 64)
        }
    }

    return [ordered]@{
        format_version = 1
        fixture = $FixtureName
        offline_only = $true
        protocol = [ordered]@{
            layout_major = Get-Int32 $Region 4
            layout_minor = Get-Int32 $Region 8
            byte_order = 'little'
        }
        header = [ordered]@{
            magic_hex = '{0:x8}' -f [UInt32](Get-Int32 $Region 0)
            header_length = Get-Int32 $Region 12
            total_bytes = Get-Int64 $Region 16
            slot_count = Get-Int32 $Region 24
            lease_record_count = Get-Int32 $Region 28
            max_key_bytes = Get-Int32 $Region 32
            max_descriptor_bytes = Get-Int32 $Region 36
            max_value_bytes = Get-Int32 $Region 40
            index_entry_count = Get-Int32 $Region 44
            index_entry_size = Get-Int32 $Region 48
            index_offset = Get-Int64 $Region 56
            index_length = Get-Int64 $Region 64
            lease_registry_offset = Get-Int64 $Region 72
            lease_registry_length = Get-Int64 $Region 80
            slot_metadata_offset = Get-Int64 $Region 88
            slot_metadata_length = Get-Int64 $Region 96
            descriptor_storage_offset = Get-Int64 $Region 104
            descriptor_storage_length = Get-Int64 $Region 112
            payload_storage_offset = Get-Int64 $Region 120
            payload_storage_length = Get-Int64 $Region 128
            store_id_hex = '{0:x16}' -f [UInt64](Get-Int64 $Region 136)
            store_state = Get-Int32 $Region 144
            sequence = Get-Int64 $Region 152
        }
        index_entries = $indexEntries
        lease_records = $leaseRecords
        slots = $slots
    }
}

$binaryKey = ConvertFrom-Hex '0001ff80'
$binaryHash = [Convert]::ToUInt64('4653dd7f9a76930d', 16)
$helloKey = ConvertFrom-Hex '68656c6c6f'
$helloHash = [Convert]::ToUInt64('a430d84680aabd0b', 16)
$singleFfKey = ConvertFrom-Hex 'ff'
$singleFfHash = [Convert]::ToUInt64('af64724c8602eb6e', 16)

$fixtureNames = @('empty', 'published', 'pending-reservation', 'pending-removal', 'reused-slot')
foreach ($fixtureName in $fixtureNames) {
    $region = New-CanonicalRegion

    switch ($fixtureName) {
        'published' {
            Set-Int64 $region 152 1
            Set-IndexEntry $region 5 1 $binaryHash 0 1 0 $binaryKey
            Set-Slot $region 0 2 1 0 0 $binaryKey (ConvertFrom-Hex 'd0007f') (ConvertFrom-Hex '000102ff80') 4242 0 $binaryHash 1
        }
        'pending-reservation' {
            Set-IndexEntry $region 3 1 $helloHash 1 1 0 $helloKey
            Set-Slot $region 1 1 1 0 0 $helloKey (ConvertFrom-Hex 'aa00') (ConvertFrom-Hex '10002000000000') 4343 3 $helloHash 0
        }
        'pending-removal' {
            Set-Int64 $region 152 9
            Set-IndexEntry $region 6 1 $singleFfHash 2 1 0 $singleFfKey
            Set-Slot $region 2 3 1 0 1 $singleFfKey (ConvertFrom-Hex 'beef') (ConvertFrom-Hex 'de00adbe') 4444 0 $singleFfHash 8
            Set-Lease $region 2 2 1 0 5555 9
        }
        'reused-slot' {
            Set-Int64 $region 152 2
            Set-IndexEntry $region 3 2 $helloHash 0 1 0 $helloKey
            Set-IndexEntry $region 5 1 $binaryHash 0 2 0 $binaryKey
            Set-Slot $region 0 2 2 0 0 $binaryKey (ConvertFrom-Hex '0102') (ConvertFrom-Hex '99008877') 4646 0 $binaryHash 2
        }
    }

    $binaryPath = Join-Path $fixtureRoot "$fixtureName.bin"
    [IO.File]::WriteAllBytes($binaryPath, $region)

    $snapshot = Get-NormalizedSnapshot $fixtureName $region
    $jsonOptions = [Text.Json.JsonSerializerOptions]::new()
    $jsonOptions.WriteIndented = $true
    $json = [Text.Json.JsonSerializer]::Serialize($snapshot, $jsonOptions) + "`n"
    [IO.File]::WriteAllText(
        (Join-Path $fixtureRoot "$fixtureName.snapshot.json"),
        $json.Replace("`r`n", "`n"),
        $utf8WithoutBom)
}

Write-Host "Generated $($fixtureNames.Count) deterministic layout-v1.2 fixture pairs in $fixtureRoot."
