namespace CodingAgent.Pipeline.Models;

/// <summary>Describes how a path changed in the brain repository.</summary>
public enum BrainPathChangeKind { Added, Modified, Deleted }

/// <summary>A single changed path in the brain repository (spec 054, task 2B.1).</summary>
public sealed record BrainPathChange(string Path, BrainPathChangeKind Kind);
