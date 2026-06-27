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
        var descriptorTarget = new Span<byte>(_region.Pointer + slot.DescriptorOffset, descriptor.Length);
        descriptor.CopyTo(descriptorTarget);

        var valueTarget = new Span<byte>(_region.Pointer + slot.PayloadOffset, value.Length);
        value.CopyTo(valueTarget);
    }
}
