using SharedMemoryStore.Layout;

namespace SharedMemoryStore.Options;

internal static class SharedMemoryStoreOptionsValidator
{
    public static StoreOpenStatus Validate(SharedMemoryStoreOptions? options, out StoreLayout layout)
    {
        layout = default;

        if (options is null
            || string.IsNullOrWhiteSpace(options.Name)
            || options.Name.IndexOf('\0') >= 0
            || options.Name.Length > 240
            || options.SlotCount <= 0
            || options.MaxKeyBytes <= 0
            || options.MaxDescriptorBytes < 0
            || options.MaxValueBytes <= 0
            || options.LeaseRecordCount <= 0
            || options.TotalBytes <= 0)
        {
            return StoreOpenStatus.InvalidOptions;
        }

        try
        {
            layout = StoreLayout.FromOptions(options);
        }
        catch (OverflowException)
        {
            return StoreOpenStatus.InvalidOptions;
        }

        return layout.FitsWithinTotalBytes()
            ? StoreOpenStatus.Success
            : StoreOpenStatus.InsufficientCapacity;
    }
}
