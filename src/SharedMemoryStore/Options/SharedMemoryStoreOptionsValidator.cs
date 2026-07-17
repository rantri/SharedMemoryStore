using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.Options;

internal static class SharedMemoryStoreOptionsValidator
{
    public static StoreOpenStatus Validate(SharedMemoryStoreOptions? options, out StoreLayoutV2 layout)
    {
        return ValidateDetailed(options, out layout).Status;
    }

    public static StoreOptionsValidationResult ValidateDetailed(SharedMemoryStoreOptions? options, out StoreLayoutV2 layout)
    {
        layout = default;

        if (options is null)
        {
            return Invalid(new StoreOptionsValidationFailure(nameof(options), "Options are required."));
        }

        var failures = new List<StoreOptionsValidationFailure>();
        if (string.IsNullOrWhiteSpace(options.Name))
        {
            failures.Add(new StoreOptionsValidationFailure(nameof(options.Name), "Name is required."));
        }
        else
        {
            if (options.Name.IndexOf('\0') >= 0)
            {
                failures.Add(new StoreOptionsValidationFailure(nameof(options.Name), "Name must not contain null characters."));
            }

            if (options.Name.Length > 240)
            {
                failures.Add(new StoreOptionsValidationFailure(nameof(options.Name), "Name must be 240 characters or fewer."));
            }
        }

        if (!Enum.IsDefined(options.OpenMode))
        {
            failures.Add(new StoreOptionsValidationFailure(nameof(options.OpenMode), "OpenMode must be a defined value."));
        }

        if (options.ParticipantRecordCount < 1 || options.ParticipantRecordCount > 1_048_575)
        {
            failures.Add(new StoreOptionsValidationFailure(
                nameof(options.ParticipantRecordCount),
                "ParticipantRecordCount must be between 1 and 1,048,575."));
        }

        if (options.SlotCount <= 0)
        {
            failures.Add(new StoreOptionsValidationFailure(nameof(options.SlotCount), "SlotCount must be greater than zero."));
        }
        else if (options.SlotCount > LayoutV2Constants.MaximumSlotCount)
        {
            failures.Add(new StoreOptionsValidationFailure(
                nameof(options.SlotCount),
                "SlotCount must be between 1 and 1,048,575."));
        }

        if (options.MaxKeyBytes <= 0)
        {
            failures.Add(new StoreOptionsValidationFailure(nameof(options.MaxKeyBytes), "MaxKeyBytes must be greater than zero."));
        }

        if (options.MaxDescriptorBytes < 0)
        {
            failures.Add(new StoreOptionsValidationFailure(nameof(options.MaxDescriptorBytes), "MaxDescriptorBytes must be zero or greater."));
        }

        if (options.MaxValueBytes <= 0)
        {
            failures.Add(new StoreOptionsValidationFailure(nameof(options.MaxValueBytes), "MaxValueBytes must be greater than zero."));
        }

        if (options.LeaseRecordCount <= 0)
        {
            failures.Add(new StoreOptionsValidationFailure(nameof(options.LeaseRecordCount), "LeaseRecordCount must be greater than zero."));
        }

        if (options.TotalBytes <= 0)
        {
            failures.Add(new StoreOptionsValidationFailure(nameof(options.TotalBytes), "TotalBytes must be greater than zero."));
        }

        if (failures.Count != 0)
        {
            return Invalid(failures);
        }

        try
        {
            layout = StoreLayoutV2.FromOptions(options);
        }
        catch (OverflowException)
        {
            return Invalid(new StoreOptionsValidationFailure(nameof(options.TotalBytes), "Layout size calculation overflowed."));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Invalid(new StoreOptionsValidationFailure(exception.ParamName ?? nameof(options), exception.Message));
        }

        if (!layout.FitsWithinTotalBytes())
        {
            return new StoreOptionsValidationResult(
                StoreOpenStatus.InsufficientCapacity,
                new[]
                {
                    new StoreOptionsValidationFailure(
                        nameof(options.TotalBytes),
                        $"TotalBytes must be at least {layout.RequiredBytes} for the configured capacities.")
                });
        }

        return new StoreOptionsValidationResult(StoreOpenStatus.Success, Array.Empty<StoreOptionsValidationFailure>());
    }

    private static StoreOptionsValidationResult Invalid(StoreOptionsValidationFailure failure)
    {
        return Invalid(new[] { failure });
    }

    private static StoreOptionsValidationResult Invalid(IReadOnlyList<StoreOptionsValidationFailure> failures)
    {
        return new StoreOptionsValidationResult(StoreOpenStatus.InvalidOptions, failures);
    }
}
