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

{{/*
Secret env vars injected into every component:
  AGENT_API_KEY           — required for API authentication
  OTEL_EXPORTER_OTLP_HEADERS — optional telemetry auth header

Usage (inside an env: list, indented to 12):
  {{- include "coding-agent-automation.secretEnv" . | nindent 12 }}
*/}}
{{- define "coding-agent-automation.secretEnv" -}}
- name: AGENT_API_KEY
  valueFrom:
    secretKeyRef:
      name: {{ include "coding-agent-automation.secretName" . }}
      key: agent-api-key
- name: OTEL_EXPORTER_OTLP_HEADERS
  valueFrom:
    secretKeyRef:
      name: {{ include "coding-agent-automation.secretName" . }}
      key: otel-headers
      optional: true
{{- end }}

{{/*
OpenTelemetry env vars. Accepts a dict with "serviceName" and "root" keys.
Renders OTEL_SERVICE_NAME, OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL.
OTEL_EXPORTER_OTLP_HEADERS is handled by secretEnv.

Usage (inside an env: list, indented to 12):
  {{- include "coding-agent-automation.otelEnv" (dict "serviceName" "coding-agent-api" "root" .) | nindent 12 }}
*/}}
{{- define "coding-agent-automation.otelEnv" -}}
- name: OTEL_SERVICE_NAME
  value: {{ .serviceName | quote }}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ .root.Values.otel.endpoint | quote }}
- name: OTEL_EXPORTER_OTLP_PROTOCOL
  value: {{ .root.Values.otel.protocol | quote }}
- name: OTEL_RESOURCE_ATTRIBUTES
  value: {{ .root.Values.otel.resourceAttributes | quote }}
{{- end }}

{{/*
WorkDistribution env vars shared by api, jobcontroller, and orchestrator.
Renders all WorkDistribution__* keys as env list items.

Usage (inside an env: list, indented to 12):
  {{- include "coding-agent-automation.workDistributionEnv" . | nindent 12 }}
*/}}
{{- define "coding-agent-automation.workDistributionEnv" -}}
- name: WorkDistribution__OrchestratorUrl
  value: {{ include "coding-agent-automation.agentOrchestratorUrl" . | quote }}
- name: WorkDistribution__AgentApiKeySecretName
  value: {{ include "coding-agent-automation.secretName" . | quote }}
- name: WorkDistribution__AgentServiceAccountName
  value: "{{ include "coding-agent-automation.fullname" . }}-agent"
- name: WorkDistribution__Namespace
  valueFrom:
    fieldRef:
      fieldPath: metadata.namespace
- name: WorkDistribution__OpencodeConfigSecretName
  value: {{ include "coding-agent-automation.secretName" . | quote }}
- name: WorkDistribution__JobTemplatesPath
  value: "/app/config/job-templates.yaml"
- name: WorkDistribution__Dispatch__IntervalSeconds
  value: {{ .Values.workDistribution.dispatch.intervalSeconds | quote }}
- name: WorkDistribution__Dispatch__RateLimitPerSecond
  value: {{ .Values.workDistribution.dispatch.rateLimitPerSecond | quote }}
- name: WorkDistribution__Dispatch__AgentJobTimeoutSeconds
  value: {{ .Values.workDistribution.dispatch.agentJobTimeoutSeconds | quote }}
- name: WorkDistribution__Dispatch__ChatPodConnectTimeoutSeconds
  value: {{ .Values.workDistribution.dispatch.chatPodConnectTimeoutSeconds | quote }}
- name: WorkDistribution__Dispatch__ChatTerminationGracePeriodSeconds
  value: {{ .Values.workDistribution.dispatch.chatTerminationGracePeriodSeconds | quote }}
- name: WorkDistribution__Dispatch__ChatIdleTimeoutSeconds
  value: {{ .Values.workDistribution.dispatch.chatIdleTimeoutSeconds | quote }}
{{- range $i, $pvc := (.Values.credentialPools).kiro | default list }}
- name: WorkDistribution__CredentialPools__Kiro__{{ $i }}
  value: {{ $pvc | quote }}
{{- end }}
{{- end }}

{{/*
WorkDistribution ConfigMap data block for orchestrator-env-configmap.
Same values as workDistributionEnv but rendered as flat key: value pairs
(no env list wrapper) for use in ConfigMap .data.
Namespace is a literal release namespace here since fieldRef is not available in ConfigMaps.

Usage (inside ConfigMap data:, indented to 2):
  {{- include "coding-agent-automation.workDistributionConfigMapData" . | nindent 2 }}
*/}}
{{- define "coding-agent-automation.workDistributionConfigMapData" -}}
WorkDistribution__OrchestratorUrl: {{ include "coding-agent-automation.agentOrchestratorUrl" . | quote }}
WorkDistribution__AgentApiKeySecretName: {{ include "coding-agent-automation.secretName" . | quote }}
WorkDistribution__AgentServiceAccountName: "{{ include "coding-agent-automation.fullname" . }}-agent"
WorkDistribution__Namespace: {{ .Release.Namespace | quote }}
WorkDistribution__OpencodeConfigSecretName: {{ include "coding-agent-automation.secretName" . | quote }}
WorkDistribution__JobTemplatesPath: "/app/config/job-templates.yaml"
WorkDistribution__Dispatch__IntervalSeconds: {{ .Values.workDistribution.dispatch.intervalSeconds | quote }}
WorkDistribution__Dispatch__RateLimitPerSecond: {{ .Values.workDistribution.dispatch.rateLimitPerSecond | quote }}
WorkDistribution__Dispatch__AgentJobTimeoutSeconds: {{ .Values.workDistribution.dispatch.agentJobTimeoutSeconds | quote }}
WorkDistribution__Dispatch__ChatPodConnectTimeoutSeconds: {{ .Values.workDistribution.dispatch.chatPodConnectTimeoutSeconds | quote }}
WorkDistribution__Dispatch__ChatTerminationGracePeriodSeconds: {{ .Values.workDistribution.dispatch.chatTerminationGracePeriodSeconds | quote }}
WorkDistribution__Dispatch__ChatIdleTimeoutSeconds: {{ .Values.workDistribution.dispatch.chatIdleTimeoutSeconds | quote }}
WorkDistribution__Reconciliation__IntervalSeconds: {{ .Values.workDistribution.reconciliation.intervalSeconds | quote }}
WorkDistribution__Reconciliation__StaleRetentionDays: {{ .Values.workDistribution.reconciliation.staleRetentionDays | quote }}
{{- range $i, $pvc := (.Values.credentialPools).kiro | default list }}
WorkDistribution__CredentialPools__Kiro__{{ $i }}: {{ $pvc | quote }}
{{- end }}
{{- end }}

{{/*
SignalR Redis env var. Renders the env entry only when connectionString is set.

Usage (inside an env: list, indented to 12):
  {{- include "coding-agent-automation.signalrEnv" . | nindent 12 }}
*/}}
{{- define "coding-agent-automation.signalrEnv" -}}
{{- if .Values.signalr.redis.connectionString }}
- name: SignalR__Redis__ConnectionString
  value: {{ .Values.signalr.redis.connectionString | quote }}
{{- end }}
{{- end }}

{{/*
Leader election lease name. Accepts a dict with "override", "suffix", and "root" keys.
Returns the override value if non-empty, otherwise "caa-<release>-<suffix>".

Usage:
  value: {{ include "coding-agent-automation.leaseName" (dict "override" .Values.jobController.leaderElection.dispatchLeaseName "suffix" "dispatch-lock" "root" .) }}
*/}}
{{- define "coding-agent-automation.leaseName" -}}
{{- if .override -}}
{{- .override | quote -}}
{{- else -}}
{{- printf "caa-%s-%s" .root.Release.Name .suffix | quote -}}
{{- end -}}
{{- end }}
