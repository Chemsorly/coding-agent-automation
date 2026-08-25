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
{{- end -}}
