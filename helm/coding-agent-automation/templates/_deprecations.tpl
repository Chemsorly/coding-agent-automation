{{- define "coding-agent-automation.deprecations" -}}
{{- if and (not .Values.api.enabled) (empty .Values.api.baseUrl) }}
  {{- fail "api.baseUrl must be set when api.enabled is false. The orchestrator will compute an in-cluster URL pointing to a non-existent service, causing runtime failures on first HTTP call. Set api.baseUrl to the URL of an externally deployed Pipeline API, or enable api.enabled to deploy it in this release." }}
{{- end }}
{{- if and (not .Values.scheduler.enabled) (empty .Values.scheduler.baseUrl) }}
  {{- fail "scheduler.baseUrl must be set when scheduler.enabled is false. The orchestrator will compute an in-cluster URL pointing to a non-existent service, causing LoopStatusPollingService to fail on every poll. Set scheduler.baseUrl to the URL of an externally deployed Scheduler, or enable scheduler.enabled to deploy it in this release." }}
{{- end }}
{{- if hasKey (.Values.database | default dict) "enabled" }}
  {{- fail "database.enabled is no longer supported. PostgreSQL is required. Set database.host and database.auth.existingSecret, then remove database.enabled from your values." }}
{{- end }}
{{- if hasKey (.Values.workDistribution | default dict) "mode" }}
  {{- fail "workDistribution.mode is removed. Only Kubernetes mode is supported. Remove workDistribution.mode from your values." }}
{{- end }}
{{- if .Values.agents }}
  {{- fail "agents[] is removed. Define agent pod specs in jobTemplates[] instead. See NOTES.txt for the migration." }}
{{- end }}
{{- if hasKey (.Values.orchestrator | default dict) "persistence" }}
  {{- fail "orchestrator.persistence is removed. Configuration now lives in PostgreSQL. Remove orchestrator.persistence from your values." }}
{{- end }}
{{- $redis := (.Values.signalr | default dict).redis | default dict }}
{{- if and (gt (int (.Values.api.replicas | default 1)) 1) (empty $redis.connectionString) }}
  {{- fail "api.replicas is greater than 1 but signalr.redis.connectionString is empty. Without a Redis backplane the API's AgentRegistryService and OrchestratorRunService keep agent/run state in-memory per pod, so SignalR hub messages cannot be routed across replicas (split-brain state, dropped agent events). Spec 048 Phase 2 makes the API a hard dependency of the Web UI, so a multi-replica API MUST share state via Redis. Set signalr.redis.connectionString to a Redis connection string, or set api.replicas: 1 for a single-replica (in-memory) deployment." }}
{{- end }}
{{- end -}}
