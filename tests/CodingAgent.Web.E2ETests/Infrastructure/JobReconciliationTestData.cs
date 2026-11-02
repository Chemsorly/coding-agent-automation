using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Shared seed helper for job reconciliation E2E tests. Produces a Running work item backed by
/// an in-memory K8s Job so that <see cref="CodingAgent.JobController.Reconciliation.ReconciliationLoop"/>
/// exercises the ExitCodeFailure, Timeout, and backoff-guard paths.
/// </summary>
internal static class JobReconciliationTestData
{
    /// <summary>
    /// Adds an issue with labels ["agent:in-progress"], inserts a WorkItem row (selector "jc-e2e",
    /// TimeoutSeconds 3600, Default project), transitions it to Dispatched with DispatchedAt = UtcNow and
    /// K8sJobName = JobNameFactory.ForBrain(id), then to Running; adds the PipelineRun to Fixture.RunService;
    /// adds a V1Job to CreatedJobs with labels app.kubernetes.io/managed-by=caa-orchestrator,
    /// app.kubernetes.io/component=agent-job and caa/work-item-id=id, CreationTimestamp = UtcNow, and an empty Status.
    /// Returns the id and the Job name.
    /// </summary>
    public static async Task<(Guid Id, string JobName)> SeedRunningItemWithJobAsync(
        E2EFixture fixture, string issueIdentifier)
    {
        // Step 1: add issue to fake provider so label-swap callbacks have an issue to act on.
        fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueIdentifier,
            Title = "Test issue",
            Description = "## Requirements\nTest\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = ["agent:in-progress"]
        });

        // Step 2: insert WorkItem row directly, mirroring InsertPendingWorkItemAsync.
        // All fields must be set explicitly — WorkItemEntity uses empty-string defaults, not
        // required, so missing fields compile but leave silent empty-string values.
        var workItemId = Guid.NewGuid();
        await using var db = fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Pending,
            Payload = "{}",
            AgentSelector = "jc-e2e",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
        });
        await db.SaveChangesAsync();

        // Step 3: transition to Dispatched with K8sJobName and DispatchedAt.
        var jobName = JobNameFactory.ForBrain(workItemId);
        var transitionService = fixture.ApiServices.GetRequiredService<WorkItemTransitionService>();
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Dispatched, w =>
        {
            w.DispatchedAt = DateTimeOffset.UtcNow;
            w.K8sJobName = jobName;
        });

        // Step 4: transition to Running.
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Running);

        // Step 5: add PipelineRun to RunService so FailRunWithLabelAsync has a run to act on
        // when PostStatusAsync triggers WorkItemStatusTransitionService on failure.
        // Without this, FailRunCoreAsync silently returns null and no label fires.
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = workItemId.ToString(),
            IssueIdentifier = new IssueIdentifier(issueIdentifier),
            IssueTitle = "Test issue",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            RunType = PipelineRunType.Implementation,
            InitiatedBy = "jc-e2e-test"
        });
        fixture.RunService.AddRun(run);

        // Step 6: add a bare V1Job to CreatedJobs with the labels ReconciliationLoop.ListJobsAsync
        // filters on ("app.kubernetes.io/managed-by=caa-orchestrator").
        var fakeJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["app.kubernetes.io/component"] = "agent-job",
                    ["caa/work-item-id"] = workItemId.ToString()
                },
                CreationTimestamp = DateTime.UtcNow
            },
            Status = new V1JobStatus()
        };
        fixture.K8sClient.CreatedJobs[jobName] = fakeJob;

        return (workItemId, jobName);
    }
}
