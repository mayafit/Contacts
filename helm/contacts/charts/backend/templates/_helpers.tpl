{{- define "backend.fullname" -}}
{{- printf "%s-backend" .Release.Name | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "backend.labels" -}}
app.kubernetes.io/name: backend
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: backend
app.kubernetes.io/part-of: contacts
{{- end }}

{{- define "backend.selectorLabels" -}}
app.kubernetes.io/name: backend
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- /* The agent template of /vault/secrets/env: the annotation and the AD-49 agent config render the same */ -}}
{{- define "backend.vaultEnvTemplate" -}}
{{`{{ with secret "`}}{{ .Values.vault.kvPath }}{{`" }}`}}
{{`{{ range $k, $v := .Data.data }}`}}
export {{`{{ $k }}`}}="{{`{{ $v }}`}}"
{{`{{ end }}`}}
{{- if .Values.vault.redisPasswordKey }}
{{`{{ with index .Data.data "`}}{{ .Values.vault.redisPasswordKey }}{{`" }}`}}
export Redis__ConnectionString="{{ include "backend.redisHost" . }},password={{`{{ . }}`}}"
{{`{{ end }}`}}
{{- end }}
{{`{{ end }}`}}
{{- end }}

{{- /* The Redis master Service as StackExchange.Redis takes it (host:port) */ -}}
{{- define "backend.redisHost" -}}
{{- printf "%s-redis-master:%v" .Release.Name (.Values.global.redis.port | int) -}}
{{- end }}
