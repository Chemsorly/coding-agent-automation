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

{{/*
Common secret env vars (API key + OTEL headers) injected into every deployment.
Usage: {{ include "coding-agent-automation.commonSecretEnv" . }}
*/}}
{{- define "coding-agent-automation.commonSecretEnv" -}}
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
OTEL endpoint env vars. Accepts a dict with keys "root" (.) and "serviceName" (string).
Usage: {{ include "coding-agent-automation.otelEnv" (dict "root" . "serviceName" "coding-agent-api") }}
*/}}
{{- define "coding-agent-automation.otelEnv" -}}
- name: OTEL_SERVICE_NAME
  value: {{ .serviceName | quote }}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ .root.Values.otel.endpoint | quote }}
- name: OTEL_EXPORTER_OTLP_PROTOCOL
  value: {{ .root.Values.otel.protocol | quote }}
{{- end }}

{{/*
WorkDistribution env vars shared by api and jobcontroller.
Usage: {{ include "coding-agent-automation.workDistributionEnv" . }}
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
- name: WorkDistribution__Dispatch__ChatSessionMaxDurationSeconds
  value: {{ .Values.workDistribution.dispatch.chatSessionMaxDurationSeconds | quote }}
- name: WorkDistribution__Dispatch__ChatPodConnectTimeoutSeconds
  value: {{ .Values.workDistribution.dispatch.chatPodConnectTimeoutSeconds | quote }}
- name: WorkDistribution__Dispatch__ChatTerminationGracePeriodSeconds
  value: {{ .Values.workDistribution.dispatch.chatTerminationGracePeriodSeconds | quote }}
- name: WorkDistribution__Reconciliation__IntervalSeconds
  value: {{ .Values.workDistribution.reconciliation.intervalSeconds | quote }}
- name: WorkDistribution__Reconciliation__StaleRetentionDays
  value: {{ .Values.workDistribution.reconciliation.staleRetentionDays | quote }}
{{- range $i, $pvc := (.Values.credentialPools).kiro | default list }}
- name: WorkDistribution__CredentialPools__Kiro__{{ $i }}
  value: {{ $pvc | quote }}
{{- end }}
{{- end }}

{{/*
Standard ClusterIP service for a named component.
Accepts a dict with keys:
  root        — top-level context (.)
  component   — component label value (e.g. "api")
  port        — service port number
  serviceType — service type (e.g. "ClusterIP")
Usage:
  {{- include "coding-agent-automation.componentService" (dict "root" . "component" "api" "port" .Values.api.service.port "serviceType" .Values.api.service.type) }}
*/}}
{{- define "coding-agent-automation.componentService" -}}
apiVersion: v1
kind: Service
metadata:
  name: {{ include "coding-agent-automation.fullname" .root }}-{{ .component }}
  labels:
    {{- include "coding-agent-automation.labels" .root | nindent 4 }}
    app.kubernetes.io/component: {{ .component }}
spec:
  type: {{ .serviceType }}
  selector:
    {{- include "coding-agent-automation.selectorLabels" .root | nindent 4 }}
    app.kubernetes.io/component: {{ .component }}
  ports:
    - name: http
      port: {{ .port }}
      targetPort: http
      protocol: TCP
{{- end }}
