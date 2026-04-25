using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KiroCliLib.Models;
using Xunit;

namespace KiroWebUI.Tests.Properties;

/// <summary>
/// Property 7: File Change Grouping by Type
/// Migrated from KiroCliPoc.Tests as part of ARC-11 (#146).
/// Validates: Requirements 8.1
/// </summary>
public class FileChangeGroupingPropertyTests
{
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

    /// <summary>
    /// Property 7: Grouping by FileChangeType produces groups where every item
    /// has matching type, and union of all groups equals original list.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(Generators) })]
    public void GroupByType_PreservesAllItems_WithCorrectTypes(List<FileChange> fileChanges)
    {
        // Group by type
        var grouped = fileChanges.GroupBy(fc => fc.Type).ToList();

        // Every item in each group has the matching type
        foreach (var group in grouped)
        {
            foreach (var item in group)
            {
                Assert.Equal(group.Key, item.Type);
            }
        }

        // Union of all groups equals original list (no items lost or duplicated)
        var totalInGroups = grouped.Sum(g => g.Count());
        Assert.Equal(fileChanges.Count, totalInGroups);

        // Verify the actual items are the same (by reference)
        var allFromGroups = grouped.SelectMany(g => g).ToList();
        Assert.Equal(fileChanges.Count, allFromGroups.Count);

        // Each original item appears exactly once in the grouped result
        foreach (var original in fileChanges)
        {
            Assert.Contains(original, allFromGroups);
        }
    }

    /// <summary>
    /// Property 7: Groups only contain the expected FileChangeType values.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(Generators) })]
    public void GroupByType_OnlyContainsValidTypes(List<FileChange> fileChanges)
    {
        var validTypes = new HashSet<FileChangeType>(Enum.GetValues<FileChangeType>());
        var grouped = fileChanges.GroupBy(fc => fc.Type);

        foreach (var group in grouped)
        {
            Assert.Contains(group.Key, validTypes);
        }
    }
}
