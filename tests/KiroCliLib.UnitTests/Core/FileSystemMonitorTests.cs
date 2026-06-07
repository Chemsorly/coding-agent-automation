using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KiroCliLib.Core;
using KiroCliLib.Models;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Unit tests for FileSystemMonitor.
/// Tests snapshot comparison logic (CompareSnapshots) and input validation.
/// ScanWorkspace requires real filesystem and is tested in integration tests.
/// </summary>
public class FileSystemMonitorTests
{
    private readonly FileSystemMonitor _monitor = new();

    // --- Input validation ---

    [Fact]
    public void ScanWorkspace_NullDirectory_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _monitor.ScanWorkspace(null!));
    }

    [Fact]
    public void ScanWorkspace_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _monitor.ScanWorkspace("/nonexistent/path/abc123"));
    }

    [Fact]
    public void CompareSnapshots_NullBefore_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _monitor.CompareSnapshots(null!, Array.Empty<FileSnapshot>()));
    }

    [Fact]
    public void CompareSnapshots_NullAfter_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _monitor.CompareSnapshots(Array.Empty<FileSnapshot>(), null!));
    }

    // --- CompareSnapshots logic ---

    [Fact]
    public void CompareSnapshots_BothEmpty_ReturnsNoChanges()
    {
        var changes = _monitor.CompareSnapshots(
            Array.Empty<FileSnapshot>(),
            Array.Empty<FileSnapshot>());

        Assert.Empty(changes);
    }

    [Fact]
    public void CompareSnapshots_IdenticalSnapshots_ReturnsNoChanges()
    {
        var timestamp = DateTime.UtcNow;
        var snapshot = new List<FileSnapshot>
        {
            new() { Path = "src/file1.cs", LastModified = timestamp },
            new() { Path = "src/file2.cs", LastModified = timestamp },
        };

        var changes = _monitor.CompareSnapshots(snapshot, snapshot);

        Assert.Empty(changes);
    }

    [Fact]
    public void CompareSnapshots_NewFileInAfter_DetectedAsCreated()
    {
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var before = new List<FileSnapshot>
        {
            new() { Path = "src/existing.cs", LastModified = timestamp },
        };
        var after = new List<FileSnapshot>
        {
            new() { Path = "src/existing.cs", LastModified = timestamp },
            new() { Path = "src/new-file.cs", LastModified = timestamp },
        };

        var changes = _monitor.CompareSnapshots(before, after);

        var created = Assert.Single(changes);
        Assert.Equal("src/new-file.cs", created.Path);
        Assert.Equal(FileChangeType.Created, created.Type);
    }

    [Fact]
    public void CompareSnapshots_FileRemovedInAfter_DetectedAsDeleted()
    {
        var timestamp = DateTime.UtcNow;
        var before = new List<FileSnapshot>
        {
            new() { Path = "src/keep.cs", LastModified = timestamp },
            new() { Path = "src/removed.cs", LastModified = timestamp },
        };
        var after = new List<FileSnapshot>
        {
            new() { Path = "src/keep.cs", LastModified = timestamp },
        };

        var changes = _monitor.CompareSnapshots(before, after);

        var deleted = Assert.Single(changes);
        Assert.Equal("src/removed.cs", deleted.Path);
        Assert.Equal(FileChangeType.Deleted, deleted.Type);
    }

    [Fact]
    public void CompareSnapshots_FileTimestampChanged_DetectedAsModified()
    {
        var before = new List<FileSnapshot>
        {
            new() { Path = "src/file.cs", LastModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        };
        var after = new List<FileSnapshot>
        {
            new() { Path = "src/file.cs", LastModified = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc) },
        };

        var changes = _monitor.CompareSnapshots(before, after);

        var modified = Assert.Single(changes);
        Assert.Equal("src/file.cs", modified.Path);
        Assert.Equal(FileChangeType.Modified, modified.Type);
    }

    [Fact]
    public void CompareSnapshots_MixedChanges_DetectsAllTypes()
    {
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var before = new List<FileSnapshot>
        {
            new() { Path = "src/unchanged.cs", LastModified = timestamp },
            new() { Path = "src/modified.cs", LastModified = timestamp },
            new() { Path = "src/deleted.cs", LastModified = timestamp },
        };
        var after = new List<FileSnapshot>
        {
            new() { Path = "src/unchanged.cs", LastModified = timestamp },
            new() { Path = "src/modified.cs", LastModified = timestamp.AddSeconds(5) },
            new() { Path = "src/created.cs", LastModified = timestamp },
        };

        var changes = _monitor.CompareSnapshots(before, after);

        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c.Path == "src/modified.cs" && c.Type == FileChangeType.Modified);
        Assert.Contains(changes, c => c.Path == "src/created.cs" && c.Type == FileChangeType.Created);
        Assert.Contains(changes, c => c.Path == "src/deleted.cs" && c.Type == FileChangeType.Deleted);
    }

    [Fact]
    public void CompareSnapshots_OlderTimestampInAfter_NotDetectedAsModified()
    {
        // If the after timestamp is older (or equal), it should NOT be detected as modified
        var before = new List<FileSnapshot>
        {
            new() { Path = "src/file.cs", LastModified = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) },
        };
        var after = new List<FileSnapshot>
        {
            new() { Path = "src/file.cs", LastModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        };

        var changes = _monitor.CompareSnapshots(before, after);

        Assert.Empty(changes);
    }

    // --- Property-based tests ---

    /// <summary>
    /// Property: Grouping file changes by type produces groups where every item
    /// has matching type, and union of all groups equals original list.
    /// Migrated from CodingAgentWebUI.IntegrationTests.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(Generators) })]
    public void GroupByType_PreservesAllItems_WithCorrectTypes(List<FileChange> fileChanges)
    {
        var grouped = fileChanges.GroupBy(fc => fc.Type).ToList();

        foreach (var group in grouped)
            foreach (var item in group)
                Assert.Equal(group.Key, item.Type);

        Assert.Equal(fileChanges.Count, grouped.Sum(g => g.Count()));
    }

    /// <summary>
    /// Property: CompareSnapshots detects the correct number of changes
    /// when given disjoint before/after sets (all created + all deleted).
    /// </summary>
    [Property(MaxTest = 20)]
    public bool CompareSnapshots_DisjointSets_AllCreatedAndDeleted(byte[] seeds)
    {
        if (seeds == null || seeds.Length < 2) return true;

        var timestamp = DateTime.UtcNow;
        var beforeCount = seeds[0] % 10;
        var afterCount = seeds[1] % 10;

        var before = Enumerable.Range(0, beforeCount)
            .Select(i => new FileSnapshot { Path = $"before/file{i}.cs", LastModified = timestamp })
            .ToList();
        var after = Enumerable.Range(0, afterCount)
            .Select(i => new FileSnapshot { Path = $"after/file{i}.cs", LastModified = timestamp })
            .ToList();

        var changes = _monitor.CompareSnapshots(before, after);

        var createdCount = changes.Count(c => c.Type == FileChangeType.Created);
        var deletedCount = changes.Count(c => c.Type == FileChangeType.Deleted);

        return createdCount == afterCount && deletedCount == beforeCount;
    }

    public static class Generators
    {
        public static Arbitrary<List<FileChange>> FileChangeList()
        {
            var fileChangeTypes = Enum.GetValues<FileChangeType>();
            var singleFileChangeGen =
                from typeIdx in Gen.Choose(0, fileChangeTypes.Length - 1)
                from pathIdx in Gen.Choose(0, 999)
                select new FileChange
                {
                    Path = $"src/file{pathIdx}.cs",
                    Type = fileChangeTypes[typeIdx]
                };
            var gen = Gen.Choose(0, 30)
                .SelectMany(count =>
                    singleFileChangeGen.ArrayOf(count)
                    .Select(arr => arr.ToList()));
            return gen.ToArbitrary();
        }
    }
}
