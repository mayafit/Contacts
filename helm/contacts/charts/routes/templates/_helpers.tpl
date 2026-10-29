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

{{- define "routes.backendSelectorLabels" -}}
app.kubernetes.io/name: backend
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "routes.frontendSelectorLabels" -}}
app.kubernetes.io/name: frontend
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}
