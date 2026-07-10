using System.Buffers;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore.Slots;

internal sealed unsafe class SlotWriter
{
    private readonly MemoryMappedStoreRegion _region;

    public SlotWriter(MemoryMappedStoreRegion region)
    {
        _region = region;
    }

    public void Write(ref SharedSlotMetadata slot, ReadOnlySpan<byte> value, ReadOnlySpan<byte> descriptor)
    {
        WriteDescriptor(ref slot, descriptor);

        var valueTarget = new Span<byte>(_region.Pointer + slot.PayloadOffset, value.Length);
        value.CopyTo(valueTarget);
    }

    public void WriteDescriptor(ref SharedSlotMetadata slot, ReadOnlySpan<byte> descriptor)
    {
        var descriptorTarget = new Span<byte>(_region.Pointer + slot.DescriptorOffset, descriptor.Length);
        descriptor.CopyTo(descriptorTarget);
    }

    public void WriteSegments(ref SharedSlotMetadata slot, in ReadOnlySequence<byte> payload, out long copiedBytes)
    {
        copiedBytes = 0;
        foreach (var segment in payload)
        {
            if (segment.Length > payload.Length - copiedBytes)
            {
                throw new InvalidDataException("The segmented payload exceeded its announced sequence length.");
            }

            var destination = new Span<byte>(
                _region.Pointer + slot.PayloadOffset + copiedBytes,
                segment.Length);
            segment.Span.CopyTo(destination);
            copiedBytes += segment.Length;
        }
    }
}
