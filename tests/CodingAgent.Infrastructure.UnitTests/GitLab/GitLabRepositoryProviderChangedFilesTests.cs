using AwesomeAssertions;
using CodingAgent.Infrastructure.GitLab;
using CodingAgent.Pipeline.Models;
using Moq;
using NGitLab;
using NGitLab.Models;
using Xunit;

namespace CodingAgent.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Moq-based tests for <see cref="GitLabRepositoryProvider.GetPullRequestChangedFilesAsync"/>.
/// </summary>
public class GitLabRepositoryProviderChangedFilesTests
{
    private const string BaseBranch = "main";
    private const int ProjectId = 1;

    private static GitLabRepositoryProvider CreateProvider(IMergeRequestClient mrClient)
    {
        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetMergeRequest(It.IsAny<NGitLab.Models.ProjectId>()))
            .Returns(mrClient);
        return new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);
    }

    [Fact]
    public async Task GetChangedFiles_ListsFilesAndHead()
    {
        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[7L]).Returns(new MergeRequest
        {
            Iid = 7,
            ChangesCount = "2",
            DiffRefs = new DiffRefs { HeadSha = "h1" }
        });
        mrClientMock.Setup(c => c.GetDiffsAsync(7L))
            .Returns(new Diffs(
                new Diff { NewPath = "a.cs", OldPath = "a.cs" },
                new Diff { NewPath = "b.cs", OldPath = "b.cs" }));

        var result = await CreateProvider(mrClientMock.Object)
            .GetPullRequestChangedFilesAsync(7, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Files.Should().BeEquivalentTo(["a.cs", "b.cs"]);
        result.IsComplete.Should().BeTrue();
        result.HeadSha.Should().Be("h1");
    }

    [Fact]
    public async Task GetChangedFiles_RenameListsBothPaths()
    {
        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[7L]).Returns(new MergeRequest
        {
            Iid = 7,
            ChangesCount = "1",
            DiffRefs = new DiffRefs { HeadSha = "h1" }
        });
        mrClientMock.Setup(c => c.GetDiffsAsync(7L))
            .Returns(new Diffs(
                new Diff { NewPath = "new-name.cs", OldPath = "sonar-project.properties" }));

        var result = await CreateProvider(mrClientMock.Object)
            .GetPullRequestChangedFilesAsync(7, CancellationToken.None);

        result!.Files.Should().BeEquivalentTo(["new-name.cs", "sonar-project.properties"]);
    }

    [Fact]
    public async Task GetChangedFiles_AtTheCap_IsIncomplete()
    {
        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[7L]).Returns(new MergeRequest
        {
            Iid = 7,
            ChangesCount = "1000+",
            DiffRefs = new DiffRefs { HeadSha = "h1" }
        });
        mrClientMock.Setup(c => c.GetDiffsAsync(7L)).Returns(new Diffs());

        var result = await CreateProvider(mrClientMock.Object)
            .GetPullRequestChangedFilesAsync(7, CancellationToken.None);

        result!.IsComplete.Should().BeFalse("1000+ means the cap was hit");
    }

    [Fact]
    public async Task GetChangedFiles_CountNotComputedYet_IsIncomplete()
    {
        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[7L]).Returns(new MergeRequest
        {
            Iid = 7,
            ChangesCount = null,
            DiffRefs = new DiffRefs { HeadSha = "h1" }
        });
        mrClientMock.Setup(c => c.GetDiffsAsync(7L)).Returns(new Diffs());

        var result = await CreateProvider(mrClientMock.Object)
            .GetPullRequestChangedFilesAsync(7, CancellationToken.None);

        result!.IsComplete.Should().BeFalse("null ChangesCount means still computing");
    }

    [Fact]
    public async Task GetChangedFiles_NoDiffRefs_HasEmptyHead()
    {
        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[7L]).Returns(new MergeRequest
        {
            Iid = 7,
            ChangesCount = "1",
            DiffRefs = null
        });
        mrClientMock.Setup(c => c.GetDiffsAsync(7L))
            .Returns(new Diffs(new Diff { NewPath = "a.cs", OldPath = "a.cs" }));

        var result = await CreateProvider(mrClientMock.Object)
            .GetPullRequestChangedFilesAsync(7, CancellationToken.None);

        result!.HeadSha.Should().Be(string.Empty);
    }

    /// <summary>
    /// Concrete subclass of the abstract <see cref="GitLabCollectionResponse{T}"/>
    /// that yields a fixed array of items for test use.
    /// </summary>
    private sealed class Diffs(params Diff[] items) : GitLabCollectionResponse<Diff>
    {
        public override IEnumerator<Diff> GetEnumerator() =>
            ((IEnumerable<Diff>)items).GetEnumerator();

        public override async IAsyncEnumerator<Diff> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            foreach (var item in items)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }
}
