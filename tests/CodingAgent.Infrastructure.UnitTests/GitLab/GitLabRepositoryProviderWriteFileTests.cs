using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Infrastructure.GitLab;
using CodingAgent.Pipeline.Models;
using Moq;
using NGitLab;
using NGitLab.Models;
using System.Net;
using Xunit;

namespace CodingAgent.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Tests for <see cref="GitLabRepositoryProvider.WriteFileToBaseBranchAsync"/>.
/// Spec 054 req 5.
/// </summary>
public class GitLabRepositoryProviderWriteFileTests
{
    private const string BaseBranch = "main";
    private const int ProjectId = 1;
    private const string FortyAHexInput = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // Sha1 constructor input (lowercase)
    private static readonly string FortyAHexExpected = new('A', 40); // Sha1.ToString() returns uppercase

    private static (GitLabRepositoryProvider Provider, Mock<IGitLabClient> ClientMock,
        Mock<IFilesClient> FilesClientMock, Mock<ICommitClient> CommitClientMock,
        List<CommitCreate> CapturedCreates)
        CreateProvider(bool fileExists)
    {
        var capturedCreates = new List<CommitCreate>();

        var filesClientMock = new Mock<IFilesClient>();
        filesClientMock
            .Setup(f => f.FileExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fileExists);

        var commitClientMock = new Mock<ICommitClient>();
        commitClientMock
            .Setup(c => c.Create(It.IsAny<CommitCreate>()))
            .Callback<CommitCreate>(capturedCreates.Add)
            .Returns(new Commit { Id = new Sha1(FortyAHexInput) });

        var repoClientMock = new Mock<IRepositoryClient>();
        repoClientMock.Setup(r => r.Files).Returns(filesClientMock.Object);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetRepository(It.IsAny<NGitLab.Models.ProjectId>())).Returns(repoClientMock.Object);
        clientMock.Setup(c => c.GetCommits(It.IsAny<NGitLab.Models.ProjectId>())).Returns(commitClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);
        return (provider, clientMock, filesClientMock, commitClientMock, capturedCreates);
    }

    [Fact]
    public async Task WriteFile_NewFile_CreatesWithAuthorAndMessage()
    {
        var (provider, _, _, _, capturedCreates) = CreateProvider(fileExists: false);

        var result = await provider.WriteFileToBaseBranchAsync(
            "inbox/2026-01-01_run.md", "content text", "brain: inbox from run", CancellationToken.None);

        result.CommitSha.Should().Be(FortyAHexExpected);

        capturedCreates.Should().HaveCount(1);
        var create = capturedCreates[0];
        create.Branch.Should().Be(BaseBranch);
        create.CommitMessage.Should().Be("brain: inbox from run");
        create.AuthorName.Should().Be(GitConstants.CommitAuthorName);
        create.AuthorEmail.Should().Be(GitConstants.CommitAuthorEmail);
        create.Actions.Should().HaveCount(1);
        var action = create.Actions[0];
        action.Action.Should().Be("create");
        action.FilePath.Should().Be("inbox/2026-01-01_run.md");
        action.Content.Should().Be("content text");
        action.Encoding.Should().Be("text");
    }

    [Fact]
    public async Task WriteFile_ExistingFile_Updates()
    {
        var (provider, _, _, _, capturedCreates) = CreateProvider(fileExists: true);

        await provider.WriteFileToBaseBranchAsync(
            "inbox/2026-01-01_run.md", "updated content", "brain: inbox update", CancellationToken.None);

        capturedCreates.Should().HaveCount(1);
        capturedCreates[0].Actions[0].Action.Should().Be("update");
    }

    [Fact]
    public async Task WriteFile_BadRequest_ThrowsRemoteFileConflict()
    {
        var filesClientMock = new Mock<IFilesClient>();
        filesClientMock
            .Setup(f => f.FileExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var commitClientMock = new Mock<ICommitClient>();
        commitClientMock
            .Setup(c => c.Create(It.IsAny<CommitCreate>()))
            .Throws(new GitLabException("A file with this name already exists") { StatusCode = HttpStatusCode.BadRequest });

        var repoClientMock = new Mock<IRepositoryClient>();
        repoClientMock.Setup(r => r.Files).Returns(filesClientMock.Object);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetRepository(It.IsAny<NGitLab.Models.ProjectId>())).Returns(repoClientMock.Object);
        clientMock.Setup(c => c.GetCommits(It.IsAny<NGitLab.Models.ProjectId>())).Returns(commitClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);

        var act = () => provider.WriteFileToBaseBranchAsync(
            "inbox/2026-01-01_run.md", "content", "msg", CancellationToken.None);

        await act.Should().ThrowAsync<RemoteFileConflictException>();
    }

    [Fact]
    public async Task WriteFile_Forbidden_IsNotAConflict()
    {
        var filesClientMock = new Mock<IFilesClient>();
        filesClientMock
            .Setup(f => f.FileExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var commitClientMock = new Mock<ICommitClient>();
        commitClientMock
            .Setup(c => c.Create(It.IsAny<CommitCreate>()))
            .Throws(new GitLabException("Forbidden") { StatusCode = HttpStatusCode.Forbidden });

        var repoClientMock = new Mock<IRepositoryClient>();
        repoClientMock.Setup(r => r.Files).Returns(filesClientMock.Object);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetRepository(It.IsAny<NGitLab.Models.ProjectId>())).Returns(repoClientMock.Object);
        clientMock.Setup(c => c.GetCommits(It.IsAny<NGitLab.Models.ProjectId>())).Returns(commitClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);

        var act = () => provider.WriteFileToBaseBranchAsync(
            "inbox/2026-01-01_run.md", "content", "msg", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Should().NotBeOfType<RemoteFileConflictException>(
            "403 Forbidden is not a conflict and must not be mapped to RemoteFileConflictException");
    }
}
