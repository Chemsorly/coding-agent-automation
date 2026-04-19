using FsCheck;
using FsCheck.Xunit;
using KiroWebUI.Pipeline.Services;
using Xunit;

namespace KiroWebUI.Tests.Pipeline;

public class BlacklistMatcherPropertyTests
{
    [Property(MaxTest = 100)]
    public bool PathUnderPrefix_AlwaysMatches(NonEmptyString prefix, NonEmptyString suffix)
    {
        var cleanPrefix = prefix.Get.Replace("/", "").Replace("\\", "");
        if (string.IsNullOrEmpty(cleanPrefix)) return true;
        var path = $"{cleanPrefix}/{suffix.Get}";
        return BlacklistMatcher.IsBlacklisted(path, new[] { cleanPrefix });
    }

    [Property(MaxTest = 100)]
    public bool MatchingIsCaseInsensitive(NonEmptyString prefix, NonEmptyString suffix)
    {
        var cleanPrefix = prefix.Get.Replace("/", "").Replace("\\", "");
        if (string.IsNullOrEmpty(cleanPrefix)) return true;
        var path = $"{cleanPrefix.ToUpperInvariant()}/{suffix.Get}";
        return BlacklistMatcher.IsBlacklisted(path, new[] { cleanPrefix.ToLowerInvariant() });
    }

    [Property(MaxTest = 100)]
    public bool UnrelatedPath_NeverMatches(PositiveInt seed)
    {
        var path = $"src/file{seed.Get}.cs";
        return !BlacklistMatcher.IsBlacklisted(path, new[] { ".kiro", ".github" });
    }

    [Property(MaxTest = 100)]
    public bool EmptyBlacklist_NeverMatches(NonEmptyString path)
    {
        return !BlacklistMatcher.IsBlacklisted(path.Get, Array.Empty<string>());
    }
}
