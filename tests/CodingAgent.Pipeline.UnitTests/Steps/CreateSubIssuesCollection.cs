using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Serializes <see cref="CreateSubIssuesStepTests"/> and <see cref="CreateSubIssuesStepRoutingTests"/>
/// to prevent concurrent execution. Both classes call <see cref="CodingAgent.Pipeline.Services.Steps.CreateSubIssuesStep.ExecuteAsync"/>
/// which emits on the static <c>PipelineTelemetry.SubIssuesCreated</c> counter. When run in
/// parallel, one class's emissions bleed into the other's <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// callback, causing snapshot-delta and ContainSingle assertions to see an inflated count.
/// </summary>
[CollectionDefinition("CreateSubIssues")]
public class CreateSubIssuesCollection;
