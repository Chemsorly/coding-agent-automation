# Bootstrap Guide

How to set up a fresh Kubernetes deployment or migrate configuration from an existing instance.

<!-- Spec 045 Task 8b: config import/export endpoints moved to the API service (port :8080). -->

## Scenario A — Fresh Install

1. Deploy the Helm chart:
   ```bash
   helm install coding-agent ./helm/coding-agent-automation \
     --set secrets.agentApiKey="$(openssl rand -hex 32)" \
     --set database.host=postgres.coding-agent.svc.cluster.local \
     --set database.auth.existingSecret=postgres-secret
   ```

2. Open the web UI. A first-run banner will appear prompting you to configure job templates.

3. Go to **Settings** and configure:
   - Providers (Issue, Repository, Agent, optionally Pipeline/CI)
   - Agent Profiles, Quality Gate Configs, Reviewer Configs
   - Pipeline Job Templates

4. Create a pipeline job template and start a run, or enable closed-loop mode to process `agent:next` issues automatically.

> After upgrading from Spec 041 to a later release, start the pipeline loop manually from the web UI on first boot. Closed-loop auto-start is restored in Spec 045.

---

## Scenario B — Migrate from an Existing Instance (HTTP export/import)

Use the HTTP API to export configuration from the old instance and import it into the new one.

> ⚠️ **`POST /api/config/import` is destructive.** It clears ALL existing configuration (providers, profiles, quality gate configs, reviewer configs, projects, and job templates) before inserting the uploaded bundle. Run history, work items, and consolidation data are preserved. The operation is transactional — it fully commits or fully rolls back.

> **Authentication:** Both endpoints require the `Authorization: Bearer <OPERATOR_API_KEY>` header with the operator-tier key. Agent pod keys (derived keys) are rejected with HTTP 403. Use the master key from `secrets.agentApiKey` in your Helm values for bootstrap operations. See [Authentication](#authentication) below.

### Step 1 — Export from the old instance

```bash
curl -H "Authorization: Bearer $OPERATOR_API_KEY" \
  -o pipeline-config-export.json \
  https://old-instance:8080/api/config/export
```

The response is a JSON file download (`pipeline-config-export.json`) containing all configuration as a single bundle. **Provider secrets (Settings and Secrets dictionary values) are exported unredacted** — the bundle contains live credentials. This is intentional: the import endpoint writes the bundle verbatim, so a redacted export would restore every credential as `"****"` and silently break every provider. The endpoint is operator-tier gated; the UI warns before download.

> **Security note:** Treat the export file as a secret. Delete it after a successful import. Do not commit it to version control. If you need to archive it, encrypt it at rest.

### Step 2 — Import into the new instance

The import endpoint accepts a `multipart/form-data` POST with a single form field named `file`:

```bash
curl -X POST \
  -H "Authorization: Bearer $OPERATOR_API_KEY" \
  -F "file=@pipeline-config-export.json" \
  https://new-instance:8080/api/config/import
```

On success, the response body is:

```json
{
  "success": true,
  "message": "Imported: 2 providers, 1 profiles, 1 quality gates, 1 reviewers, 1 projects, 1 templates"
}
```

On validation failure (invalid JSON, empty bundle, no file), HTTP 400 is returned with an error message.

### Authentication

Both endpoints require the `Authorization: Bearer <OPERATOR_API_KEY>` header. The operator API key is the master key set via the `secrets.agentApiKey` Helm value (or the `AGENT_API_KEY` environment variable in the API pod). Agent-derived keys — held by agent pods — receive HTTP 403 on these endpoints (Tier 2 enforcement per Spec 042 Req 6.5).

See [HTTP API Reference](api-reference.md) for full endpoint details.

---

## Scenario C — File-Based Auto-Import (First-Boot Migration)

On first startup against an empty Postgres database, `DatabaseStartupService.ImportJsonConfigIfNeededAsync` checks for JSON config files under `ConfigBaseDirectory` (default: `/app/config/pipeline`). If files are present, they are imported automatically without any manual action.

This is a zero-effort migration path for operators upgrading from a docker-compose deployment that stored configuration in JSON files.

**How to use it:**

1. Mount your existing JSON config directory at `/app/config/pipeline` in the orchestrator pod (e.g., via a PVC or ConfigMap volume).
2. Start the orchestrator. On first boot it detects the files and imports them.
3. After import succeeds, the volume mount is no longer needed — configuration lives in Postgres.

On a fresh install with no JSON files present, this is a no-op.

---

## Bundle Format

The export bundle is a flat JSON object with arrays for each entity type:

```json
{
  "pipelineConfig": "{...}",
  "providerConfigs": [ ... ],
  "agentProfiles": [ ... ],
  "qualityGateConfigs": [ ... ],
  "reviewerConfigs": [ ... ],
  "projects": [ ... ],
  "jobTemplates": [ ... ]
}
```

The `pipelineConfig`, per-entity `configuration`, and project `settings` fields contain serialized JSON strings (double-encoded). This is the format the system uses internally and is preserved in the export.

For a full example response see [HTTP API Reference — GET /api/config/export](api-reference.md#get-apiconfigexport).
