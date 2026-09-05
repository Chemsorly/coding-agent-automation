namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Marker interface for models that expose a nullable creation timestamp,
/// used for FIFO ordering in the pipeline loop.
/// </summary>
public interface IHasCreatedAt
{
    DateTime? CreatedAt { get; }
}
