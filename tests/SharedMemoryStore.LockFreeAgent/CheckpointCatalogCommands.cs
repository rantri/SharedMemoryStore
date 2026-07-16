using System.Text.Json;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.LockFreeAgent;

/// <summary>
/// Emits the production checkpoint inventory for external qualification tools.
/// Keeping the catalog in one place prevents a newly appended checkpoint from
/// silently disappearing from cross-process pause qualification.
/// </summary>
internal static class CheckpointCatalogCommands
{
    internal static int Run(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            return 64;
        }

        CheckpointCatalogEntry[] entries = LockFreeCheckpointCatalog.Entries
            .Select(static entry => new CheckpointCatalogEntry(
                (int)entry.Id,
                entry.Id.ToString(),
                entry.Family.ToString(),
                entry.Position.ToString(),
                entry.Pause.ToString(),
                entry.Crash.ToString(),
                entry.Race.ToString(),
                entry.IsPublicOrderingPoint,
                entry.Description))
            .ToArray();
        Console.WriteLine(JsonSerializer.Serialize(entries));
        return 0;
    }

    private readonly record struct CheckpointCatalogEntry(
        int Id,
        string Name,
        string Family,
        string Position,
        string Pause,
        string Crash,
        string Race,
        bool IsPublicOrderingPoint,
        string Description);
}
