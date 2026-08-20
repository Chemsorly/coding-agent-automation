{{/*
Expand the name of the chart.
*/}}
{{- define "coding-agent-automation.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "coding-agent-automation.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "coding-agent-automation.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "coding-agent-automation.labels" -}}
helm.sh/chart: {{ include "coding-agent-automation.chart" . }}
{{ include "coding-agent-automation.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "coding-agent-automation.selectorLabels" -}}
app.kubernetes.io/name: {{ include "coding-agent-automation.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Agent-specific fully qualified name.
Used inside range loops where dot is rebound to the agent entry.
Accepts a dict with "agentName" and "root" keys.
Usage: {{ include "coding-agent-automation.agentFullname" (dict "agentName" .name "root" $) }}
*/}}
{{- define "coding-agent-automation.agentFullname" -}}
{{- printf "%s-%s" (include "coding-agent-automation.fullname" .root) .agentName | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
ServiceAccount name
*/}}
{{- define "coding-agent-automation.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "coding-agent-automation.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
Secret name — either existing or chart-managed
*/}}
{{- define "coding-agent-automation.secretName" -}}
{{- if .Values.existingSecret }}
{{- .Values.existingSecret }}
{{- else }}
{{- include "coding-agent-automation.fullname" . }}
{{- end }}
{{- end }}

{{/*
URL that agent pods use to reach the Pipeline API (injected as ORCHESTRATOR_URL).

Every process that builds an agent Job spec must resolve this identically:
  - the Job Controller (work-item pods, via DispatchLoop)
  - the Pipeline API (consolidation and model-fetch pods, via DispatchLifecycleService)
  - the orchestrator/monolith (chat pods, via ChatJobDispatcher)

The API is the sole host of /hubs/agent and /api/work-items/* from Spec 044 onward, so this
must never resolve to the orchestrator Service — agent pods pointed there fail to connect to
the hub and cannot fetch their assignment.
*/}}
{{- define "coding-agent-automation.agentOrchestratorUrl" -}}
{{- if .Values.api.serviceUrl -}}
{{- .Values.api.serviceUrl -}}
{{- else if .Values.api.baseUrl -}}
{{- .Values.api.baseUrl -}}
{{- else -}}
{{- printf "http://%s-api.%s.svc.cluster.local:%d" (include "coding-agent-automation.fullname" .) .Release.Namespace (.Values.api.service.port | int) -}}
{{- end -}}
{{- end }}

{{/*
Base URL that in-cluster components (orchestrator, Job Controller) use to reach the
Pipeline API over HTTP. Honours api.baseUrl so an externally deployed API
(api.enabled=false) is reachable, and otherwise derives the in-cluster Service URL.
*/}}
{{- define "coding-agent-automation.apiBaseUrl" -}}
{{- if .Values.api.baseUrl -}}
{{- .Values.api.baseUrl -}}
{{- else -}}
{{- printf "http://%s-api.%s.svc.cluster.local:%d" (include "coding-agent-automation.fullname" .) .Release.Namespace (.Values.api.service.port | int) -}}
{{- end -}}
{{- end }}
