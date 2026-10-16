namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Which detection agent a refactoring scan runs. <see cref="All"/> runs every agent.
/// Serialized as an integer over MessagePack: append new members, never reorder.
/// </summary>
public enum RefactoringScanFocus
{
    All = 0,
    Structure = 1,
    Correctness = 2,
    Design = 3,
    Tests = 4
}
