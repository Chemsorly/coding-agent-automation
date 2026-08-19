namespace CodingAgentWebUI;

// Database startup initialization removed in Spec 045 Task 9 (Req 1.4, 7.3):
// the monolith no longer has a Postgres connection. DatabaseStartupService.HandleMigrationsAsync
// is still called by CodingAgentWebUI.Api — the class is retained there.
// This file is an empty stub retained to avoid missing-symbol errors in any remaining references.
public static class DatabaseStartupExtensions
{
}
