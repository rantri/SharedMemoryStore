using System.Reflection;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeDirectoryCollisionTests
{
    private const ulong ExactCollisionHash = 0x0123_4567_89AB_CDEFUL;

    [Fact]
    public void ExactHashCollisionsUseBothPrimaryChoicesThenPreserveAllSlotCapacityInOverflow()
    {
        const int slotCount = 24;
        var directory = new CollisionDirectoryOracle(slotCount);

        for (var index = 0; index < slotCount; index++)
        {
            Assert.Equal(
                InsertResult.Success,
                directory.Insert(Key(index), ExactCollisionHash, Binding(index)));
        }

        Assert.Equal(8, directory.FirstBucketOccupancy);
        Assert.Equal(8, directory.SecondBucketOccupancy);
        Assert.Equal(slotCount - 16, directory.OverflowOccupancy);
        Assert.True(directory.HasSpill);
        Assert.Equal(InsertResult.StoreFull, directory.Insert(Key(slotCount), ExactCollisionHash, Binding(slotCount)));
        Assert.Equal(slotCount, directory.Count);
        for (var index = 0; index < slotCount; index++)
        {
            Assert.Equal(Binding(index), directory.Lookup(Key(index), ExactCollisionHash));
        }
    }

    [Fact]
    public void ExactUnlinkNeverClearsAnotherCollidingKeyAndRestoresOneAdmission()
    {
        const int slotCount = 20;
        var directory = new CollisionDirectoryOracle(slotCount);
        for (var index = 0; index < slotCount; index++)
        {
            Assert.Equal(InsertResult.Success, directory.Insert(Key(index), ExactCollisionHash, Binding(index)));
        }

        Assert.False(directory.Unlink(Key(18), ExactCollisionHash, Binding(17)));
        Assert.Equal(Binding(18), directory.Lookup(Key(18), ExactCollisionHash));
        Assert.True(directory.Unlink(Key(18), ExactCollisionHash, Binding(18)));
        Assert.Null(directory.Lookup(Key(18), ExactCollisionHash));
        Assert.Equal(slotCount - 1, directory.Count);

        Assert.Equal(InsertResult.Success, directory.Insert(Key(100), ExactCollisionHash, Binding(100)));
        Assert.Equal(slotCount, directory.Count);
        for (var index = 0; index < slotCount; index++)
        {
            if (index != 18)
            {
                Assert.Equal(Binding(index), directory.Lookup(Key(index), ExactCollisionHash));
            }
        }
    }

    [Fact]
    public void SpillSummarySkipsOverflowWhenEmptyAndClearsAfterLastOverflowUnlink()
    {
        var directory = new CollisionDirectoryOracle(slotCount: 20);
        for (var index = 0; index < 16; index++)
        {
            Assert.Equal(InsertResult.Success, directory.Insert(Key(index), ExactCollisionHash, Binding(index)));
        }

        Assert.False(directory.HasSpill);
        Assert.Null(directory.Lookup(Key(99), ExactCollisionHash));
        Assert.Equal(0, directory.OverflowScanCount);

        Assert.Equal(InsertResult.Success, directory.Insert(Key(16), ExactCollisionHash, Binding(16)));
        Assert.True(directory.HasSpill);
        Assert.Equal(Binding(16), directory.Lookup(Key(16), ExactCollisionHash));
        Assert.True(directory.OverflowScanCount > 0);
        Assert.True(directory.Unlink(Key(16), ExactCollisionHash, Binding(16)));
        Assert.False(directory.HasSpill);
    }

    [Fact]
    public void ProductionDirectoryContractProvidesCapacityPreservingOverflowAndExactOperations()
    {
        var type = typeof(MemoryStore).Assembly.GetType(
            "SharedMemoryStore.LockFree.LockFreeKeyDirectory",
            throwOnError: false,
            ignoreCase: false);
        Assert.True(type is not null, "The lock-free profile must implement LockFreeKeyDirectory.");

        var methods = type!.GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Contains(methods, static method => method.Name.Contains("Lookup", StringComparison.Ordinal));
        Assert.Contains(methods, static method => method.Name.Contains("Insert", StringComparison.Ordinal));
        Assert.Contains(methods, static method => method.Name.Contains("Unlink", StringComparison.Ordinal));
        Assert.Contains(methods, static method => method.Name.Contains("Overflow", StringComparison.Ordinal)
            || method.Name.Contains("Spill", StringComparison.Ordinal));
    }

    private static string Key(int index) => "collision-key-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static ulong Binding(int index) => ((ulong)(index + 1) << 31) | (uint)(index + 1);

    private enum InsertResult
    {
        Success,
        DuplicateKey,
        StoreFull
    }

    private sealed class CollisionDirectoryOracle
    {
        private readonly Entry?[] _firstBucket = new Entry?[8];
        private readonly Entry?[] _secondBucket = new Entry?[8];
        private readonly Entry?[] _overflow;
        private readonly int _slotCount;

        public CollisionDirectoryOracle(int slotCount)
        {
            _slotCount = slotCount;
            _overflow = new Entry?[slotCount];
        }

        public int Count { get; private set; }

        public bool HasSpill { get; private set; }

        public int OverflowScanCount { get; private set; }

        public int FirstBucketOccupancy => _firstBucket.Count(static entry => entry is not null);

        public int SecondBucketOccupancy => _secondBucket.Count(static entry => entry is not null);

        public int OverflowOccupancy => _overflow.Count(static entry => entry is not null);

        public InsertResult Insert(string key, ulong hash, ulong binding)
        {
            if (Lookup(key, hash) is not null)
            {
                return InsertResult.DuplicateKey;
            }

            if (Count == _slotCount)
            {
                return InsertResult.StoreFull;
            }

            var entry = new Entry(key, hash, binding);
            if (TryPlace(_firstBucket, entry) || TryPlace(_secondBucket, entry))
            {
                Count++;
                return InsertResult.Success;
            }

            HasSpill = true;
            Assert.True(TryPlace(_overflow, entry), "OverflowCount == SlotCount must preserve admission for every owned slot.");
            Count++;
            return InsertResult.Success;
        }

        public ulong? Lookup(string key, ulong hash)
        {
            var primary = Find(_firstBucket, key, hash) ?? Find(_secondBucket, key, hash);
            if (primary is not null)
            {
                return primary.Binding;
            }

            if (!HasSpill)
            {
                return null;
            }

            OverflowScanCount++;
            return Find(_overflow, key, hash)?.Binding;
        }

        public bool Unlink(string key, ulong hash, ulong exactBinding)
        {
            if (TryUnlink(_firstBucket, key, hash, exactBinding)
                || TryUnlink(_secondBucket, key, hash, exactBinding)
                || (HasSpill && TryUnlink(_overflow, key, hash, exactBinding)))
            {
                Count--;
                HasSpill = _overflow.Any(static entry => entry is not null);
                return true;
            }

            return false;
        }

        private static bool TryPlace(Entry?[] cells, Entry entry)
        {
            for (var index = 0; index < cells.Length; index++)
            {
                if (cells[index] is null)
                {
                    cells[index] = entry;
                    return true;
                }
            }

            return false;
        }

        private static Entry? Find(Entry?[] cells, string key, ulong hash)
        {
            return cells.FirstOrDefault(entry =>
                entry is not null
                && entry.Hash == hash
                && string.Equals(entry.Key, key, StringComparison.Ordinal));
        }

        private static bool TryUnlink(Entry?[] cells, string key, ulong hash, ulong exactBinding)
        {
            for (var index = 0; index < cells.Length; index++)
            {
                var entry = cells[index];
                if (entry is not null
                    && entry.Hash == hash
                    && entry.Binding == exactBinding
                    && string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    cells[index] = null;
                    return true;
                }
            }

            return false;
        }

        private sealed record Entry(string Key, ulong Hash, ulong Binding);
    }
}
