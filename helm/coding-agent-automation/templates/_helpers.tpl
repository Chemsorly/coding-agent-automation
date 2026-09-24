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
  - the web service (chat pods, via ChatJobDispatcher)

The API is the sole host of /hubs/agent and /api/work-items/* from Spec 044 onward, so this
must never resolve to the web Service — agent pods pointed there fail to connect to
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
Base URL that in-cluster components (web, Job Controller) use to reach the
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
OpenTelemetry env vars. Accepts a dict with "serviceName", "deploymentName", and "root" keys.
Renders OTEL_SERVICE_NAME, OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL,
K8S_NAMESPACE_NAME (Downward API), K8S_POD_NAME (Downward API), and OTEL_RESOURCE_ATTRIBUTES.
OTEL_EXPORTER_OTLP_HEADERS is handled by secretEnv.

The Downward API env vars (K8S_NAMESPACE_NAME, K8S_POD_NAME) are emitted BEFORE
OTEL_RESOURCE_ATTRIBUTES so that Kubernetes $(VAR_NAME) substitution resolves them correctly —
K8s only resolves $(VAR) references to variables defined earlier in the same env list.

Usage (inside an env: list, indented to 12):
  {{- include "coding-agent-automation.otelEnv" (dict "serviceName" "coding-agent-api" "deploymentName" "my-release-api" "root" .) | nindent 12 }}
*/}}
{{- define "coding-agent-automation.otelEnv" -}}
- name: OTEL_SERVICE_NAME
  value: {{ .serviceName | quote }}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ .root.Values.otel.endpoint | quote }}
- name: OTEL_EXPORTER_OTLP_PROTOCOL
  value: {{ .root.Values.otel.protocol | quote }}
- name: K8S_NAMESPACE_NAME
  valueFrom:
    fieldRef:
      fieldPath: metadata.namespace
- name: K8S_POD_NAME
  valueFrom:
    fieldRef:
      fieldPath: metadata.name
{{- $userAttrs := .root.Values.otel.resourceAttributes | trimSuffix "," }}
- name: OTEL_RESOURCE_ATTRIBUTES
  # TODO: The value is composed inside a raw YAML double-quoted string. If otel.resourceAttributes
  # contains YAML-special characters (colons, hash signs, leading/trailing whitespace) the rendered
  # YAML will be syntactically invalid and the deployment will fail. Switching the entire value to
  # use Helm's YAML-safe quoting would fix this, but the composed string also
  # contains Kubernetes $(VAR_NAME) substitution tokens which must not be Helm-quoted away.
  # A safe approach: validate that otel.resourceAttributes only contains OTel-legal characters
  # (alphanumeric, '.', '_', '-', '=', ',') via a regex or document the constraint clearly.
  # See review finding (issue #2969).
  value: "{{ if .root.Values.otel.resourceAttributes }}{{ $userAttrs }},{{ end }}k8s.deployment.name={{ .deploymentName }},k8s.namespace.name=$(K8S_NAMESPACE_NAME),k8s.pod.name=$(K8S_POD_NAME)"
{{- end }}

{{/*
WorkDistribution env vars shared by api, jobcontroller, and web.
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
{{- /* TODO [WARNING]: WorkDistribution__Dispatch__IntervalSeconds and the other Dispatch__* env vars
     below are injected into the API container but the WorkItemDispatchService that consumed them was
     removed in issue #2547. The API no longer starts a dispatch background loop, so these values are
     dead configuration for the API container (they are still used by the Scheduler, which sources
     them via its own env block in scheduler-deployment.yaml). The dead env vars are harmlessly ignored
     at runtime but may confuse operators into thinking the API still dispatches. Consider moving these
     to the Scheduler-only env block when the workDistributionEnv helper is next refactored. */}}
- name: WorkDistribution__Dispatch__IntervalSeconds
  value: {{ .Values.workDistribution.dispatch.intervalSeconds | quote }}
- name: WorkDistribution__Dispatch__RateLimitPerSecond
  value: {{ .Values.workDistribution.dispatch.rateLimitPerSecond | quote }}
- name: WorkDistribution__Dispatch__ChatJobMaxDurationSeconds
  value: {{ .Values.workDistribution.dispatch.chatJobMaxDurationSeconds | quote }}
- name: WorkDistribution__Dispatch__ChatPodConnectTimeoutSeconds
  value: {{ .Values.workDistribution.dispatch.chatPodConnectTimeoutSeconds | quote }}
- name: WorkDistribution__Dispatch__ChatTerminationGracePeriodSeconds
  value: {{ .Values.workDistribution.dispatch.chatTerminationGracePeriodSeconds | quote }}
- name: WorkDistribution__Dispatch__ChatIdleTimeoutSeconds
  value: {{ .Values.workDistribution.dispatch.chatIdleTimeoutSeconds | quote }}
- name: WorkDistribution__Dispatch__ChatReplicaCount
  value: {{ .Values.api.replicas | quote }}
{{- range $i, $pvc := (.Values.credentialPools).kiro | default list }}
- name: WorkDistribution__CredentialPools__Kiro__{{ $i }}
  value: {{ $pvc | quote }}
{{- end }}
{{- end }}

{{/*
WorkDistribution ConfigMap data block for web-env-configmap.
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
{{- /* TODO [WARNING]: WorkDistribution__Dispatch__IntervalSeconds and the other Dispatch__* keys
     below are written into the API ConfigMap but the WorkItemDispatchService that consumed them was
     removed in issue #2547. The API no longer starts a dispatch background loop, so these values are
     dead configuration for the API container. They are still used by the Scheduler (sourced from the
     same values.yaml section). The dead keys are harmlessly ignored at runtime but may mislead
     operators. Consider moving them to the Scheduler-only ConfigMap when this helper is next refactored. */}}
WorkDistribution__Dispatch__IntervalSeconds: {{ .Values.workDistribution.dispatch.intervalSeconds | quote }}
WorkDistribution__Dispatch__RateLimitPerSecond: {{ .Values.workDistribution.dispatch.rateLimitPerSecond | quote }}
WorkDistribution__Dispatch__ChatJobMaxDurationSeconds: {{ .Values.workDistribution.dispatch.chatJobMaxDurationSeconds | quote }}
WorkDistribution__Dispatch__ChatPodConnectTimeoutSeconds: {{ .Values.workDistribution.dispatch.chatPodConnectTimeoutSeconds | quote }}
WorkDistribution__Dispatch__ChatTerminationGracePeriodSeconds: {{ .Values.workDistribution.dispatch.chatTerminationGracePeriodSeconds | quote }}
WorkDistribution__Dispatch__ChatIdleTimeoutSeconds: {{ .Values.workDistribution.dispatch.chatIdleTimeoutSeconds | quote }}
WorkDistribution__Dispatch__ChatReplicaCount: {{ .Values.api.replicas | quote }}
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
