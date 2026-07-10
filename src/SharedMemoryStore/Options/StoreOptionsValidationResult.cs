namespace SharedMemoryStore.Options;

/// <summary>
/// Describes one actionable store option validation failure.
/// </summary>
/// <param name="MemberName">Name of the option member that failed validation.</param>
/// <param name="Message">Human-readable validation detail suitable for configuration diagnostics.</param>
public readonly record struct StoreOptionsValidationFailure(string MemberName, string Message);

/// <summary>
/// Public validation result for <see cref="SharedMemoryStoreOptions"/>.
/// </summary>
public sealed class StoreOptionsValidationResult
{
    internal StoreOptionsValidationResult(StoreOpenStatus status, IReadOnlyList<StoreOptionsValidationFailure> failures)
    {
        Status = status;
        Failures = Array.AsReadOnly(failures.ToArray());
    }

    /// <summary>Gets a value indicating whether the options are valid.</summary>
    public bool IsValid => Status == StoreOpenStatus.Success;

    /// <summary>Gets the open status that corresponds to the validation outcome.</summary>
    public StoreOpenStatus Status { get; }

    /// <summary>Gets actionable validation failures, or an empty collection when validation succeeds.</summary>
    public IReadOnlyList<StoreOptionsValidationFailure> Failures { get; }
}
