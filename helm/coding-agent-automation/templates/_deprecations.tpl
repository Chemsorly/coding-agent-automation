{{- define "coding-agent-automation.deprecations" -}}
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
