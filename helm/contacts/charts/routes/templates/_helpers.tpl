{{- define "routes.fullname" -}}
{{- printf "%s-routes" .Release.Name | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "routes.labels" -}}
app.kubernetes.io/name: routes
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: routes
app.kubernetes.io/part-of: contacts
{{- end }}

{{- define "routes.selectorLabels" -}}
app.kubernetes.io/name: routes
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- /* The agent template of /vault/secrets/env (the annotation and the AD-49 agent config) */ -}}
{{- define "routes.vaultEnvTemplate" -}}
{{`{{ with secret "`}}{{ .Values.vault.kvPath }}{{`" }}`}}
export Routes__ServiceToken="{{`{{ .Data.data.Routes__ServiceToken }}`}}"
export Routes__BackendToken="{{`{{ .Data.data.Routes__BackendToken }}`}}"
export Routes__ConnectionString="{{`{{ .Data.data.Routes__ConnectionString }}`}}"
{{`{{ end }}`}}
{{- end }}

{{- define "routes.backendSelectorLabels" -}}
app.kubernetes.io/name: backend
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "routes.frontendSelectorLabels" -}}
app.kubernetes.io/name: frontend
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- /* GG 11.8c: Vault over TLS is on only when the umbrella switch global.vaultTls.enabled and this chart's vault.enabled are both on. The annotations, the CA volume and mount, and the custom agent HCL all read this, so they cannot disagree. Renders "true" or nothing. */ -}}
{{- define "routes.vaultTls" -}}
{{- $t := (.Values.global).vaultTls | default dict -}}
{{- if and $t.enabled .Values.vault.enabled -}}true{{- end -}}
{{- end }}
