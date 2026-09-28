using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for provider settings CRUD (Issue, Repository, Pipeline), the Initialize Provider
/// button, and the path from repository setup-steps/secrets through the UI save to the dispatch
/// assignment (Scenario 4).
///
/// All tests that interact with provider forms use the GitLab type to avoid triggering live
/// GitHub credential validation (GitHubValidationService is a real production service in the
/// E2E harness). GitLab validation is null in the harness, so its null-guard path skips
/// credential checking. Note that SaveGitLabProvider() still requires non-empty AccessToken
/// and ProjectId — all helpers pass dummy values to satisfy these guards.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class SettingsProviderCrudTests : E2ETestBase
{
    public SettingsProviderCrudTests(E2EFixture fixture) : base(fixture) { }

    // ── Scenario 2: Issue provider CRUD ────────────────────────────────

    [Fact]
    public async Task Settings_IssueProvider_AddEditDelete()
    {
        var page = new SettingsPage(Page, BaseUrl);
        await page.NavigateAsync();
        await page.SelectTreeNodeAsync("Issue");

        // Assert: pre-seeded "E2E Issue Provider" is present
        var initial = await page.GetProviderNamesAsync();
        Assert.Contains("E2E Issue Provider", initial);

        // Act: Add a GitLab issue provider
        await page.ClickAddProviderAsync();
        await page.SelectProviderTypeAsync("GitLab");
        await page.FillGitLabProviderFormAsync("My GitLab Issues", "test-token", "99999");
        await page.ClickSaveAsync();

        // TODO [WARNING]: Missing save success assertion. The analogous agent-provider test asserts
        // IsStatusMessageVisibleAsync("saved") immediately after ClickSaveAsync(). Without this, a
        // silent save failure would produce a confusing "name not found" assertion error below rather
        // than a clear "save did not succeed" signal. Add:
        //   Assert.True(await page.IsStatusMessageVisibleAsync("saved"), "Expected save success message after add");
        // Same gap exists after the Edit and Delete saves in this test and in
        // Settings_RepositoryProvider_AddEditDelete and Settings_PipelineProvider_AddEditDelete.

        // Assert: new provider appears
        var afterAdd = await page.GetProviderNamesAsync();
        Assert.Contains("My GitLab Issues", afterAdd);

        // Capture the new provider's ID before editing
        var configs = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var newConfig = configs.FirstOrDefault(c => c.DisplayName == "My GitLab Issues");
        Assert.NotNull(newConfig);
        var originalId = newConfig.Id;

        // Act: Edit the newly added provider
        await page.ClickEditProviderAsync("My GitLab Issues");
        await page.FillGitLabProviderFormAsync("My GitLab Issues Renamed", "test-token", "99999");
        await page.ClickSaveAsync();

        // Assert: renamed
        var afterEdit = await page.GetProviderNamesAsync();
        Assert.Contains("My GitLab Issues Renamed", afterEdit);
        Assert.DoesNotContain("My GitLab Issues", afterEdit);

        // Assert: ID unchanged
        var configsAfterEdit = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var renamedConfig = configsAfterEdit.FirstOrDefault(c => c.DisplayName == "My GitLab Issues Renamed");
        Assert.NotNull(renamedConfig);
        Assert.Equal(originalId, renamedConfig.Id);

        // Seed a template that references this provider so we can observe orphan behavior
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-orphan-test",
            Name = "Orphan Template",
            IssueProviderId = originalId,
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Act: Delete the provider
        // TODO [WARNING]: The seeded "template-orphan-test" template is not cleaned up before calling
        // ClickDeleteProviderAsync. If the store or application ever adds referential-integrity constraints
        // (blocking deletion of providers still referenced by templates), this test will fail for a different
        // reason than what it is pinning (the orphan behavior), making the failure misleading. Consider
        // wrapping the delete + orphan-pin assertions in a try/finally that deletes the template, or
        // adding an explicit comment documenting the expected cleanup path.
        await page.ClickDeleteProviderAsync("My GitLab Issues Renamed");

        // Assert: removed from UI
        var afterDelete = await page.GetProviderNamesAsync();
        Assert.DoesNotContain("My GitLab Issues Renamed", afterDelete);

        // Assert: pre-seeded provider still present
        Assert.Contains("E2E Issue Provider", afterDelete);

        // Assert: template still exists pointing at the (now-deleted) provider ID.
        // BUG: deleting a provider does not cascade to templates — the template is
        // silently orphaned with no UI warning. This test PINS the current (broken) behavior.
        // Filed as a known gap: the template section should warn when a referenced provider
        // is missing. See issue tracker for follow-up.
        var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
        var orphanTemplate = templates.FirstOrDefault(t => t.Id == "template-orphan-test");
        Assert.NotNull(orphanTemplate);
        Assert.Equal(originalId, orphanTemplate.IssueProviderId); // still pointing at deleted ID
    }

    // ── Scenario 2: Repository provider CRUD ───────────────────────────

    [Fact]
    public async Task Settings_RepositoryProvider_AddEditDelete()
    {
        var page = new SettingsPage(Page, BaseUrl);
        await page.NavigateAsync();
        await page.SelectTreeNodeAsync("Repository");

        // Assert: pre-seeded "E2E Repo Provider" is present
        var initial = await page.GetProviderNamesAsync();
        Assert.Contains("E2E Repo Provider", initial);

        // Act: Add a GitLab repository provider
        await page.ClickAddProviderAsync();
        await page.SelectProviderTypeAsync("GitLab");
        await page.FillGitLabProviderFormAsync("My GitLab Repo", "test-token", "11111");
        await page.ClickSaveAsync();

        // Assert: new provider appears
        var afterAdd = await page.GetProviderNamesAsync();
        Assert.Contains("My GitLab Repo", afterAdd);

        // Capture ID
        var configs = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        var newConfig = configs.FirstOrDefault(c => c.DisplayName == "My GitLab Repo");
        Assert.NotNull(newConfig);
        var originalId = newConfig.Id;

        // Act: Edit
        await page.ClickEditProviderAsync("My GitLab Repo");
        await page.FillGitLabProviderFormAsync("My GitLab Repo Renamed", "test-token", "11111");
        await page.ClickSaveAsync();

        // Assert: renamed
        var afterEdit = await page.GetProviderNamesAsync();
        Assert.Contains("My GitLab Repo Renamed", afterEdit);
        Assert.DoesNotContain("My GitLab Repo", afterEdit);

        // Assert: ID unchanged
        var configsAfterEdit = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        var renamedConfig = configsAfterEdit.FirstOrDefault(c => c.DisplayName == "My GitLab Repo Renamed");
        Assert.NotNull(renamedConfig);
        Assert.Equal(originalId, renamedConfig.Id);

        // Act: Delete
        await page.ClickDeleteProviderAsync("My GitLab Repo Renamed");

        // Assert: removed
        var afterDelete = await page.GetProviderNamesAsync();
        Assert.DoesNotContain("My GitLab Repo Renamed", afterDelete);
        Assert.Contains("E2E Repo Provider", afterDelete);
    }

    // ── Scenario 2: Pipeline provider CRUD ─────────────────────────────

    [Fact]
    public async Task Settings_PipelineProvider_AddEditDelete()
    {
        var page = new SettingsPage(Page, BaseUrl);
        await page.NavigateAsync();
        await page.SelectTreeNodeAsync("Pipeline");

        // Act: Add a GitLab pipeline provider (no pre-seeded provider on this section)
        await page.ClickAddProviderAsync();
        await page.SelectProviderTypeAsync("GitLab");
        await page.FillGitLabProviderFormAsync("My GitLab CI", "test-token", "22222");
        await page.ClickSaveAsync();

        // Assert: new provider appears
        var afterAdd = await page.GetProviderNamesAsync();
        Assert.Contains("My GitLab CI", afterAdd);

        // Capture ID
        var configs = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, CancellationToken.None);
        var newConfig = configs.FirstOrDefault(c => c.DisplayName == "My GitLab CI");
        Assert.NotNull(newConfig);
        var originalId = newConfig.Id;

        // Act: Edit
        await page.ClickEditProviderAsync("My GitLab CI");
        await page.FillGitLabProviderFormAsync("My GitLab CI Renamed", "test-token", "22222");
        await page.ClickSaveAsync();

        // Assert: renamed
        var afterEdit = await page.GetProviderNamesAsync();
        Assert.Contains("My GitLab CI Renamed", afterEdit);
        Assert.DoesNotContain("My GitLab CI", afterEdit);

        // Assert: ID unchanged
        var configsAfterEdit = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, CancellationToken.None);
        var renamedConfig = configsAfterEdit.FirstOrDefault(c => c.DisplayName == "My GitLab CI Renamed");
        Assert.NotNull(renamedConfig);
        Assert.Equal(originalId, renamedConfig.Id);

        // Act: Delete
        await page.ClickDeleteProviderAsync("My GitLab CI Renamed");

        // Assert: removed
        var afterDelete = await page.GetProviderNamesAsync();
        Assert.DoesNotContain("My GitLab CI Renamed", afterDelete);
    }

    // ── Scenario 3: Initialize Provider button ──────────────────────────

    [Fact]
    public async Task Settings_IssueProvider_InitializeProvider_Success()
    {
        var page = new SettingsPage(Page, BaseUrl);
        await page.NavigateAsync();
        await page.SelectTreeNodeAsync("Issue");

        // Assert: pre-seeded "E2E Issue Provider" is visible
        var providers = await page.GetProviderNamesAsync();
        Assert.Contains("E2E Issue Provider", providers);

        // Act: Click the Initialize Provider button
        await page.ClickInitializeProviderAsync("E2E Issue Provider");

        // Assert: Success status appears with the expected message
        var statusText = await page.WaitForInitializeStatusAsync("E2E Issue Provider", isError: false);
        Assert.Contains("initialized successfully", statusText, StringComparison.OrdinalIgnoreCase);

        // Assert: EnsureAgentLabelsAsync was actually called on the fake (verifies the acceptance
        // criterion "EnsureAgentLabelsAsync was called on the fake issue provider").
        // InitializeAsync (default interface method) calls ValidateAsync then EnsureAgentLabelsAsync.
        Assert.Equal(1, Fixture.IssueProvider.EnsureAgentLabelsCallCount);
    }

    [Fact]
    public async Task Settings_IssueProvider_InitializeProvider_Failure()
    {
        // TODO [WARNING]: InitializeShouldFail is set here but never reset in a finally block. Because
        // ResetAll() is called at the *start* of the next test (not the end of this one), any test that
        // runs immediately after this one fails at an early assertion (before ResetAll is invoked for the
        // next test) will see a poisoned fake during its own InitializeAsync-to-first-action window.
        // Mitigate by wrapping the flag assignment and any code that could throw in a try/finally that
        // resets it, or by moving the assignment into an IAsyncLifetime.InitializeAsync override scoped
        // to this test class.

        // Arrange: set the fake to fail during ValidateAsync (called by InitializeAsync)
        Fixture.IssueProvider.InitializeShouldFail = true;

        var page = new SettingsPage(Page, BaseUrl);
        await page.NavigateAsync();
        await page.SelectTreeNodeAsync("Issue");

        // Assert: pre-seeded provider is visible
        var providers = await page.GetProviderNamesAsync();
        Assert.Contains("E2E Issue Provider", providers);

        // Act: Click Initialize
        await page.ClickInitializeProviderAsync("E2E Issue Provider");

        // Assert: Error status appears
        var statusText = await page.WaitForInitializeStatusAsync("E2E Issue Provider", isError: true);
        Assert.Contains("initialization failed", statusText, StringComparison.OrdinalIgnoreCase);

        // Assert: EnsureAgentLabelsAsync was NOT reached (ValidateAsync threw first)
        Assert.Equal(0, Fixture.IssueProvider.EnsureAgentLabelsCallCount);
    }

    // ── Scenario 4: Setup steps and secrets reach the assignment ────────

    [Fact]
    public async Task Settings_RepoProvider_SetupStepsAndSecrets_ReachAssignment()
    {
        var ct = CancellationToken.None;
        var page = new SettingsPage(Page, BaseUrl);

        // ── Step 1: Edit the pre-seeded "E2E Repo Provider" via the UI ──────────────────

        await page.NavigateAsync();
        await page.SelectTreeNodeAsync("Repository");
        await page.ClickEditProviderAsync("E2E Repo Provider");

        // Switch to GitLab type to bypass GitHub credential validation
        // TODO [WARNING]: This silently mutates the pre-seeded "E2E Repo Provider" — changing its type
        // and adding SetupSteps/Secrets — for the lifetime of this collection run. Other tests that rely
        // on "E2E Repo Provider" being the original type (or having no SetupSteps/Secrets) may break or
        // observe stale state. There is no teardown to restore the provider. Since ResetAll() + SeedDefaults()
        // runs before the next test, this is safe as long as no other test in the same run interacts with
        // this provider after this test executes. Document this assumption explicitly, or restore the
        // provider in a finally block if test ordering guarantees cannot be relied upon.
        await page.SelectProviderTypeAsync("GitLab");

        // Fill required GitLab fields
        await page.FillGitLabProviderFormAsync("E2E Repo Provider", "test-token", "12345");

        // ── Step 2: Add two setup steps ─────────────────────────────────────────────────

        await page.AddSetupStepAsync("Restore", "dotnet restore");
        await page.AddSetupStepAsync("Build", "dotnet build --no-restore");

        // ── Step 3: Add a repository secret ─────────────────────────────────────────────

        await page.AddSecretAsync("FEED_TOKEN", "abcd1234");

        // ── Step 4: Save via the UI ──────────────────────────────────────────────────────

        await page.ClickSaveAsync();

        // Verify the provider was saved with the setup steps and secrets
        var savedConfigs = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var savedRepo = savedConfigs.FirstOrDefault(c => c.Id == "repo-e2e");
        Assert.NotNull(savedRepo);
        Assert.NotNull(savedRepo.SetupSteps);
        Assert.Equal(2, savedRepo.SetupSteps.Count);
        Assert.Equal("Restore", savedRepo.SetupSteps[0].Name);
        Assert.Equal("dotnet restore", savedRepo.SetupSteps[0].Command);
        Assert.Equal("Build", savedRepo.SetupSteps[1].Name);
        Assert.Equal("dotnet build --no-restore", savedRepo.SetupSteps[1].Command);
        Assert.NotNull(savedRepo.Secrets);
        Assert.Equal("abcd1234", savedRepo.Secrets["FEED_TOKEN"]);

        // ── Step 5: Also seed a project-level secret to verify repo wins on collision ────

        // Update the default project to carry a conflicting FEED_TOKEN
        var defaultProject = await Fixture.ConfigStore.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, ct);
        Assert.NotNull(defaultProject);
        await Fixture.ConfigStore.SaveProjectAsync(defaultProject with
        {
            Secrets = new Dictionary<string, string> { ["FEED_TOKEN"] = "project-value" }
        }, ct);

        // ── Step 6: Seed prerequisites for dispatch ──────────────────────────────────────

        // Seed an issue
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "setup-100",
            Title = "Setup steps test issue",
            Description = "Test",
            Labels = new[] { "agent:next", "setup-e2e" }
        });

        // Seed an AgentProfile matching the issue's labels — required by PrepareDistributionRequestAsync
        // TODO [WARNING]: The seeded AgentProfile "profile-setup-e2e" and the issue "setup-100" are added
        // to shared fixture state that is only cleaned up by ResetAllAsync on the *next* test. If this test
        // is abandoned mid-run (e.g. distributor.DistributeAsync throws), these entries remain in the fixture
        // for subsequent tests in the collection. This is consistent with the rest of the suite's cleanup
        // pattern (ResetAll at test start), but the stray "profile-setup-e2e" AgentProfile is an untested
        // side effect that could affect tests that enumerate all agent profiles. Document the assumption that
        // ResetAll will clean it, or remove it in a finally block if test isolation is required.
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-setup-e2e",
            DisplayName = "Setup E2E Agent Profile",
            MatchLabels = new[] { "setup-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, ct);

        // ── Step 7: Connect a fake agent so FakeJobController can deliver the job ────────

        await using var agent = new FakeAgentClient("setup-agent-1", "setup-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // ── Step 8: Dispatch the issue (UI save happened before this — payload frozen at dispatch time) ──

        var orchService = Fixture.Factory.Services.GetRequiredService<IDispatchOrchestrationService>();
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var project = await Fixture.ConfigStore.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, ct)
            ?? throw new InvalidOperationException("Default project not found");

        var request = await orchService.PrepareDistributionRequestAsync(
            new ImplementationDispatchOrchestrationRequest
            {
                IssueIdentifier = "setup-100",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                BrainProviderId = null,
                PipelineProviderId = null,
                InitiatedBy = "e2e-test",
                Project = project
            }, ct);

        Assert.NotNull(request);

        var distResult = await distributor.DistributeAsync(request, ct);
        Assert.True(distResult.Success, $"Distribution failed: {distResult.ErrorMessage}");

        // ── Step 9: Wait for the agent to receive the assignment ──────────────────────────

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // ── Step 10: Assert setup steps reached the assignment ────────────────────────────

        var repoConfig = assignment.ProviderConfigs.FirstOrDefault(c => c.Id == "repo-e2e");
        Assert.NotNull(repoConfig);

        Assert.NotNull(repoConfig.SetupSteps);
        Assert.Equal(2, repoConfig.SetupSteps.Count);
        Assert.Equal("Restore", repoConfig.SetupSteps[0].Name);
        Assert.Equal("dotnet restore", repoConfig.SetupSteps[0].Command);
        Assert.Equal("Build", repoConfig.SetupSteps[1].Name);
        Assert.Equal("dotnet build --no-restore", repoConfig.SetupSteps[1].Command);

        // ── Step 11: Assert FEED_TOKEN secret reached the assignment ─────────────────────

        Assert.NotNull(repoConfig.Secrets);
        Assert.Equal("abcd1234", repoConfig.Secrets["FEED_TOKEN"]);

        // ── Step 12: Assert project secret is overridden by the repo secret ──────────────
        //
        // The assignment carries both repo secrets (in ProviderConfig.Secrets) and project
        // secrets (in JobAssignmentMessage.ProjectSecrets). The agent-side RunEnvironmentSetupStep
        // merges them with repo winning on key collision. Verify the repo secret value arrived
        // and that the project secret is also present (at a different value, demonstrating the
        // override scenario).
        //
        // TODO [WARNING]: This test does NOT verify the actual merge/override behavior. It only asserts
        // that both values are present in separate fields (repoConfig.Secrets and assignment.ProjectSecrets).
        // The acceptance criterion "a project secret with the same key is overridden by the repository value"
        // is only partially covered here — the repo wins on collision, but RunEnvironmentSetupStep.MergeSecrets
        // (the code that implements the override) is never invoked by this test. The merge itself is covered
        // by RunEnvironmentSetupStepSecretMergingTests.cs; this E2E test only verifies *delivery* of both
        // secrets to the assignment. Consider either invoking the merge step with the delivered assignment
        // to fully close the E2E coverage gap, or updating the scenario docstring to explicitly state that
        // the override assertion is deferred to the unit tests.
        Assert.NotNull(assignment.ProjectSecrets);
        Assert.Equal("project-value", assignment.ProjectSecrets["FEED_TOKEN"]);
        // The repo secret that will WIN (verified above via repoConfig.Secrets["FEED_TOKEN"])
        // is "abcd1234", not "project-value". The merge happens on the agent side at runtime.
    }
}
