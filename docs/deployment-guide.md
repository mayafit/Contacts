# Deployment Guide

**Generated:** 2026-01-10
**Workflow:** document-project v1.2.0
**Project:** WebKit Frontend
**Deployment Target:** Docker + NGINX

---

## Overview

This guide covers deployment strategies, infrastructure requirements, and operational procedures for the WebKit frontend application.

---

## Deployment Architecture

### Infrastructure Stack

```
┌─────────────────────────────────────────────┐
│           Load Balancer / Ingress           │
└────────────────┬────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────┐
│          NGINX Container (Port 8080)        │
│  ┌─────────────────────────────────────┐   │
│  │    Static Files (/usr/share/nginx)  │   │
│  │    - index.html                     │   │
│  │    - JavaScript bundles             │   │
│  │    - CSS files                      │   │
│  │    - Assets (images, fonts)         │   │
│  └─────────────────────────────────────┘   │
└─────────────────────────────────────────────┘
```

**Key Characteristics:**
- **Type:** Static Single Page Application (SPA)
- **Web Server:** NGINX 1.27.0-alpine-slim
- **Container Port:** 8080 (exposed)
- **Build:** Multi-stage Docker build
- **CDN Compatible:** Yes (static assets can be served via CDN)

---

## Build Process

### Production Build

**Command:**
```bash
npm run build:prod
```

**Output:** `dist/` folder containing:
- `index.html` - Entry point HTML
- JavaScript bundles (code-split, minified)
- CSS bundles (extracted, minified)
- Assets (images, fonts, static files)
- `env.config.json` - Environment configuration

**Build Optimizations:**
- **Code Splitting:** Automatic via webpack
- **Minification:** Terser for JS, cssnano for CSS
- **Compression:** Gzip via `compression-webpack-plugin`
- **Tree Shaking:** Dead code elimination
- **Bundle Size:** Optimized with vendor chunking

**Environment Variables:**
- `E2E="false"` - Disables E2E test code
- `--max_old_space_size=4096` - Node memory limit

---

## Docker Deployment

### Dockerfile Overview

**File:** `DockerfileFull`

**Multi-Stage Build:**

```dockerfile
# Stage 1: Build (Node.js)
FROM node:22.12.0-slim as builder
WORKDIR /usr/src/app
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build:prod

# Stage 2: Serve (NGINX)
FROM nginx:1.27.0-alpine-slim
COPY ./.nginx/nginx.conf /etc/nginx/nginx.conf
COPY ./.nginx/mime.types /etc/nginx/mime.types
COPY --from=builder /usr/src/app/dist /usr/share/nginx/html
EXPOSE 8080
CMD [ "nginx", "-g", "daemon off;" ]
```

**Build Stages:**

1. **Builder Stage:**
   - Base: `node:22.12.0-slim`
   - Installs dependencies via `npm ci`
   - Runs production build
   - Output: `/usr/src/app/dist`

2. **Runtime Stage:**
   - Base: `nginx:1.27.0-alpine-slim`
   - Copies NGINX configuration
   - Copies built static files from builder
   - Exposes port 8080
   - Runs NGINX as entry point

### Building the Docker Image

```bash
# Build image
docker build -f DockerfileFull -t webkit:latest .

# Build with specific tag
docker build -f DockerfileFull -t webkit:1.0.3 .

# Build for specific platform
docker build --platform linux/amd64 -f DockerfileFull -t webkit:latest .
```

### Running the Container Locally

```bash
# Run container
docker run -d -p 8080:8080 --name webkit webkit:latest

# Run with custom environment
docker run -d \
  -p 8080:8080 \
  -v $(pwd)/custom-env.config.json:/usr/share/nginx/html/env.config.json \
  --name webkit \
  webkit:latest

# View logs
docker logs webkit

# Stop container
docker stop webkit

# Remove container
docker rm webkit
```

**Access Application:**
`http://localhost:8080`

---

## NGINX Configuration

### Configuration Files

**Main Config:** `.nginx/nginx.conf`
**MIME Types:** `.nginx/mime.types`

**Key NGINX Features:**
- **Gzip Compression:** Enabled for text files (HTML, CSS, JS, JSON)
- **Client Max Body Size:** Configured for uploads
- **Caching Headers:** Set for static assets
- **SPA Routing:** Fallback to `index.html` for client-side routes

**Typical Configuration Highlights:**

```nginx
# Gzip Compression
gzip on;
gzip_types text/plain text/css application/json application/javascript;

# SPA Routing Support
location / {
    try_files $uri $uri/ /index.html;
}

# Cache Control for Static Assets
location ~* \.(js|css|png|jpg|jpeg|gif|ico|svg|woff|woff2|ttf|eot)$ {
    expires 1y;
    add_header Cache-Control "public, immutable";
}
```

---

## Environment Configuration

### Configuration Files

| File | Purpose | Usage |
|------|---------|-------|
| `env.config.json` | Base environment config | Runtime configuration |
| `features-flags-dev.json` | Development feature flags | Development builds |
| `features-flags-prod.json` | Production feature flags | Production builds |

### Runtime Environment Injection

**Setup Script:** `blueFiberSrc/setup.sh`

**Docker Entrypoint Flow:**
1. Container starts
2. `setup.sh` executes
3. Injects environment variables into `env.docker.js`
4. Symlink created: `/env-scripts/env.docker.js` → `/usr/share/nginx/html/env.docker.js`
5. NGINX starts

**Environment Injection Example:**

```bash
# setup.sh pseudo-code
# Generates env.docker.js from environment variables
echo "window.ENV = {" > /env-scripts/env.docker.js
echo "  API_URL: '${API_URL}'," >> /env-scripts/env.docker.js
echo "  FEATURE_FLAGS: ${FEATURE_FLAGS}" >> /env-scripts/env.docker.js
echo "};" >> /env-scripts/env.docker.js
```

**Application Access:**

```typescript
// Access injected environment in app
const apiUrl = window.ENV?.API_URL || 'http://default-api';
```

### Hardened Environments (OpenShift / Restricted)

**Required Volume Mounts:**

```yaml
volumes:
  - name: var-cache-nginx
    emptyDir: {}
  - name: var-run
    emptyDir: {}
  - name: env-scripts
    emptyDir: {}

volumeMounts:
  - name: var-cache-nginx
    mountPath: /var/cache/nginx
  - name: var-run
    mountPath: /var/run
  - name: env-scripts
    mountPath: /env-scripts
```

**Why Needed:**
- Non-root user restrictions
- Read-only filesystem restrictions
- NGINX requires writable `/var/cache/nginx` and `/var/run`
- Environment injection requires writable `/env-scripts`

---

## Kubernetes Deployment

### Deployment Manifest Example

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: webkit
  namespace: default
spec:
  replicas: 3
  selector:
    matchLabels:
      app: webkit
  template:
    metadata:
      labels:
        app: webkit
    spec:
      containers:
      - name: webkit
        image: webkit:1.0.3
        ports:
        - containerPort: 8080
          name: http
        env:
        - name: API_URL
          value: "https://api.example.com"
        - name: FEATURE_FLAGS
          value: '{"newFeature": true}'
        resources:
          requests:
            memory: "256Mi"
            cpu: "100m"
          limits:
            memory: "512Mi"
            cpu: "500m"
        livenessProbe:
          httpGet:
            path: /
            port: 8080
          initialDelaySeconds: 10
          periodSeconds: 30
        readinessProbe:
          httpGet:
            path: /
            port: 8080
          initialDelaySeconds: 5
          periodSeconds: 10
        volumeMounts:
        - name: var-cache-nginx
          mountPath: /var/cache/nginx
        - name: var-run
          mountPath: /var/run
        - name: env-scripts
          mountPath: /env-scripts
      volumes:
      - name: var-cache-nginx
        emptyDir: {}
      - name: var-run
        emptyDir: {}
      - name: env-scripts
        emptyDir: {}
---
apiVersion: v1
kind: Service
metadata:
  name: webkit
  namespace: default
spec:
  type: ClusterIP
  ports:
  - port: 80
    targetPort: 8080
    protocol: TCP
    name: http
  selector:
    app: webkit
---
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: webkit
  namespace: default
  annotations:
    kubernetes.io/ingress.class: nginx
    cert-manager.io/cluster-issuer: letsencrypt-prod
spec:
  tls:
  - hosts:
    - webkit.example.com
    secretName: webkit-tls
  rules:
  - host: webkit.example.com
    http:
      paths:
      - path: /
        pathType: Prefix
        backend:
          service:
            name: webkit
            port:
              number: 80
```

### Deployment Steps

```bash
# 1. Build and push image
docker build -f DockerfileFull -t your-registry/webkit:1.0.3 .
docker push your-registry/webkit:1.0.3

# 2. Apply Kubernetes manifests
kubectl apply -f k8s/deployment.yaml

# 3. Verify deployment
kubectl get pods -l app=webkit
kubectl get svc webkit
kubectl get ingress webkit

# 4. Check logs
kubectl logs -l app=webkit

# 5. Access application
# Via service (internal)
kubectl port-forward svc/webkit 8080:80

# Via ingress (external)
curl https://webkit.example.com
```

---

## CI/CD Pipeline

### GitHub Actions Workflow

**File:** `.github/workflows/ci.yml`

**Workflow Triggers:**
- Push to `main`, `master`, `develop` branches
- Pull requests to `main`, `master`, `develop` branches

**Workflow Steps:**

```yaml
name: CI

on:
  push:
    branches: [ main, master, develop ]
  pull_request:
    branches: [ main, master, develop ]

jobs:
  build:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        node-version: [18.x]
    steps:
      - uses: actions/checkout@v4
      - name: Use Node.js ${{ matrix.node-version }}
        uses: actions/setup-node@v4
        with:
          node-version: ${{ matrix.node-version }}
      - name: Install dependencies
        run: npm ci
      - name: Build
        run: npm run build:debug
      - name: Run tests
        run: npm run test:jest
```

**Current Status:** Basic CI workflow (lint + test + build)

**Recommended Enhancements:**

```yaml
# Additional steps to add:
- name: Lint
  run: npx eslint .

- name: Type Check
  run: npx tsc --noEmit

- name: Security Audit
  run: npm audit --audit-level=moderate

- name: Build Docker Image
  run: docker build -f DockerfileFull -t ${{ secrets.REGISTRY }}/webkit:${{ github.sha }} .

- name: Push to Registry
  run: docker push ${{ secrets.REGISTRY }}/webkit:${{ github.sha }}

- name: Deploy to Staging
  if: github.ref == 'refs/heads/develop'
  run: kubectl set image deployment/webkit webkit=${{ secrets.REGISTRY }}/webkit:${{ github.sha }}
```

### Automated Deployment Pipeline

**Recommended CI/CD Flow:**

```
┌──────────────┐
│ Code Commit  │
└──────┬───────┘
       │
       ▼
┌──────────────────┐
│ GitHub Actions   │
│ - Checkout       │
│ - Install Deps   │
│ - Lint + Test    │
│ - Build          │
└──────┬───────────┘
       │
       ▼
┌──────────────────┐      ┌──────────────────┐
│ Build Docker     │─────▶│ Push to Registry │
└──────┬───────────┘      └──────────────────┘
       │
       ▼
┌──────────────────┐
│ Deploy to K8s    │
│ - Staging (dev)  │
│ - Prod (main)    │
└──────────────────┘
```

---

## Production Checklist

### Pre-Deployment Checklist

- [ ] **Environment Variables Configured**
  - [ ] `API_URL` set correctly
  - [ ] Feature flags reviewed
  - [ ] Secrets securely stored (Kubernetes Secrets, vault, etc.)

- [ ] **Build Verification**
  - [ ] Production build succeeds: `npm run build:prod`
  - [ ] Bundle size analyzed and acceptable
  - [ ] No console errors in production build

- [ ] **Testing**
  - [ ] All unit tests pass: `npm run test:jest`
  - [ ] Manual smoke test in staging
  - [ ] Critical user flows tested
  - [ ] Performance testing completed

- [ ] **Security**
  - [ ] No security vulnerabilities: `npm audit`
  - [ ] Dependencies up to date
  - [ ] HTTPS enforced
  - [ ] Security headers configured in NGINX

- [ ] **Infrastructure**
  - [ ] Docker image built and pushed
  - [ ] Kubernetes manifests reviewed
  - [ ] Resource limits configured
  - [ ] Volume mounts configured (if hardened env)
  - [ ] Ingress/Load balancer configured
  - [ ] SSL certificates valid

- [ ] **Monitoring & Logging**
  - [ ] Application logs accessible
  - [ ] Error tracking configured (e.g., Sentry)
  - [ ] Performance monitoring (e.g., New Relic, Datadog)
  - [ ] Health check endpoints tested

- [ ] **Rollback Plan**
  - [ ] Previous version tagged and available
  - [ ] Rollback procedure documented
  - [ ] Database migration rollback (if applicable - N/A for frontend)

### Post-Deployment Checklist

- [ ] **Smoke Testing**
  - [ ] Application loads successfully
  - [ ] Critical features functional
  - [ ] No JavaScript errors in console

- [ ] **Monitoring**
  - [ ] Check error rates in logs
  - [ ] Monitor performance metrics
  - [ ] Review user analytics

- [ ] **Rollback Ready**
  - [ ] Keep previous deployment available for quick rollback

---

## Rollback Procedure

### Kubernetes Rollback

```bash
# View deployment history
kubectl rollout history deployment/webkit

# Rollback to previous version
kubectl rollout undo deployment/webkit

# Rollback to specific revision
kubectl rollout undo deployment/webkit --to-revision=2

# Monitor rollback status
kubectl rollout status deployment/webkit
```

### Docker Rollback

```bash
# Redeploy previous image version
docker pull your-registry/webkit:1.0.2
docker stop webkit
docker rm webkit
docker run -d -p 8080:8080 --name webkit your-registry/webkit:1.0.2
```

---

## Monitoring & Observability

### Health Checks

**Kubernetes Probes:**

```yaml
livenessProbe:
  httpGet:
    path: /
    port: 8080
  initialDelaySeconds: 10
  periodSeconds: 30

readinessProbe:
  httpGet:
    path: /
    port: 8080
  initialDelaySeconds: 5
  periodSeconds: 10
```

**Custom Health Endpoint (Recommended):**

Add a lightweight health check endpoint:
- `GET /health` → `{ "status": "ok", "version": "1.0.3" }`

### Logging

**Container Logs:**

```bash
# Kubernetes
kubectl logs -l app=webkit --tail=100 -f

# Docker
docker logs webkit --tail=100 -f
```

**Log Aggregation:**
- **Recommended:** Centralized logging (ELK stack, Loki, CloudWatch)
- **NGINX Access Logs:** Available in container stdout
- **Application Logs:** Console logs from React app

### Performance Monitoring

**Recommended Tools:**
- **Frontend Monitoring:** Google Analytics, Sentry, LogRocket
- **Infrastructure Monitoring:** Prometheus + Grafana
- **APM:** New Relic, Datadog

**Key Metrics:**
- **Application:**
  - Page load time
  - Time to Interactive (TTI)
  - Bundle size
  - JavaScript error rate
  - API response times

- **Infrastructure:**
  - Container CPU usage
  - Container memory usage
  - Request rate
  - Response time (p50, p95, p99)
  - Error rate (4xx, 5xx)

---

## Scaling

### Horizontal Scaling (Kubernetes)

```bash
# Scale to 5 replicas
kubectl scale deployment/webkit --replicas=5

# Autoscaling based on CPU
kubectl autoscale deployment/webkit --min=3 --max=10 --cpu-percent=70
```

**Horizontal Pod Autoscaler (HPA) Example:**

```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: webkit-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: webkit
  minReplicas: 3
  maxReplicas: 10
  metrics:
  - type: Resource
    resource:
      name: cpu
      target:
        type: Utilization
        averageUtilization: 70
  - type: Resource
    resource:
      name: memory
      target:
        type: Utilization
        averageUtilization: 80
```

### CDN Integration

**Benefits:**
- Reduced server load
- Faster global delivery
- Lower bandwidth costs

**Setup:**
1. Build application: `npm run build:prod`
2. Upload `dist/` assets to CDN
3. Update asset URLs to use CDN domain
4. Configure cache headers for optimal caching

**Example CDN Configuration:**
- **Static Assets:** `https://cdn.example.com/static/webkit/v1.0.3/`
- **Cache Duration:** 1 year (immutable assets with content hash)
- **Invalidation:** On new deployment

---

## Security Best Practices

### NGINX Security Headers

```nginx
# Recommended security headers
add_header X-Frame-Options "SAMEORIGIN" always;
add_header X-Content-Type-Options "nosniff" always;
add_header X-XSS-Protection "1; mode=block" always;
add_header Referrer-Policy "strict-origin-when-cross-origin" always;
add_header Content-Security-Policy "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline';" always;
```

### Container Security

- **Non-Root User:** Run NGINX as non-root (configure in Dockerfile)
- **Minimal Base Image:** Using `nginx:1.27.0-alpine-slim`
- **No Secrets in Image:** Use environment variables or mounted secrets
- **Image Scanning:** Scan for vulnerabilities (Trivy, Clair)

### Dependency Security

```bash
# Audit dependencies
npm audit

# Fix vulnerabilities
npm audit fix

# Update dependencies
npm update
```

---

## Troubleshooting

### Common Deployment Issues

**Issue:** Container fails to start
**Solution:**
```bash
# Check container logs
docker logs webkit

# Inspect container
docker inspect webkit

# Check NGINX config syntax
docker exec webkit nginx -t
```

**Issue:** 404 errors for client-side routes
**Solution:** Ensure NGINX config has `try_files $uri $uri/ /index.html;`

**Issue:** Static assets not loading
**Solution:** Check MIME types configuration, verify files copied to container

**Issue:** High memory usage in Node build
**Solution:** Memory limits configured in npm scripts (`--max_old_space_size`)

**Issue:** Environment variables not injected
**Solution:**
```bash
# Check setup.sh execution
docker logs webkit

# Verify env.docker.js created
docker exec webkit cat /usr/share/nginx/html/env.docker.js

# Check symlink
docker exec webkit ls -la /env-scripts/
```

---

## Additional Resources

### Related Documentation

- [Development Guide](./development-guide.md) - Local development setup
- [Source Tree Analysis](./source-tree-analysis.md) - Project structure
- [Technology Stack](./technology_stack.md) - Full tech stack
- [CI/CD Detection](./ci_cd_detection.md) - CI/CD analysis

### External Resources

- [Docker Documentation](https://docs.docker.com/)
- [Kubernetes Documentation](https://kubernetes.io/docs/)
- [NGINX Documentation](https://nginx.org/en/docs/)
- [GitHub Actions Documentation](https://docs.github.com/en/actions)

---

*This deployment guide was generated by the BMAD `document-project` workflow (exhaustive scan, Step 6).*
