// Global using statements for types moved to shared projects.
// Added as part of spec 043: leader election types moved to CodingAgentWebUI.Pipeline.LeaderElection.
// NOTE: CodingAgentWebUI.Kubernetes is NOT added as a global using here because
// 'Kubernetes' conflicts with the k8s.Kubernetes concrete type used in this project.
// Add 'using CodingAgentWebUI.Kubernetes' explicitly in files that need those types.
global using CodingAgentWebUI.Pipeline.LeaderElection;
