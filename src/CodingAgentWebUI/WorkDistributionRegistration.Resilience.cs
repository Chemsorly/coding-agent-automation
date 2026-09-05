namespace CodingAgentWebUI;

// Spec 048 Phase 2: the DB-operation Polly pipelines (RegisterResiliencePipelines, defined in
// Infrastructure.Persistence) are no longer registered in the Web host — it is Persistence-free
// and has no direct-EF consumer for them. They remain registered in the API host. See the note in
// WorkDistributionRegistration.cs (AddWorkDistribution).
public static partial class WorkDistributionRegistration
{
}
