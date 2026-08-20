// Global using statements for types moved to shared projects.
// Leader election types live in CodingAgentWebUI.Pipeline.LeaderElection (avoids per-file imports).
// NOTE: CodingAgentWebUI.Kubernetes is NOT added as a global using here because
// 'Kubernetes' conflicts with the k8s.Kubernetes concrete type used in this project.
// Add 'using CodingAgentWebUI.Kubernetes' explicitly in files that need those types.
global using CodingAgentWebUI.Pipeline.LeaderElection;
