# Deployment Guide - Contacts Application

## Overview

This document provides comprehensive deployment instructions for the Contacts application using Docker. The deployment configuration follows industry-standard security hardening practices compliant with CIS Docker Benchmark v1.6.0 and OWASP Docker Security guidelines.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Environment Configuration](#environment-configuration)
- [Building the Docker Image](#building-the-docker-image)
- [Running with Docker Compose](#running-with-docker-compose)
- [Security Features](#security-features)
- [Health Monitoring](#health-monitoring)
- [Troubleshooting](#troubleshooting)
- [Production Deployment Checklist](#production-deployment-checklist)

## Prerequisites

- Docker Engine 24.0+ or Docker Desktop
- Docker Compose 2.20+
- Node.js 18.20.4+ (for local development)
- Valid Google OAuth 2.0 credentials

## Environment Configuration

### Required Environment Variables

Create a `.env` file in the project root based on [.env.example](.env.example):

```bash
# Google OAuth Configuration
REACT_APP_GOOGLE_CLIENT_ID=your-client-id.apps.googleusercontent.com

# Google People API Base URL
REACT_APP_API_BASE_URL=https://people.googleapis.com/v1

# Environment
NODE_ENV=production
```

### Environment Variable Categories

**Non-Sensitive Configuration** (can be in `.env` or docker-compose.yml):
- `NODE_ENV` - Application environment (development, production, test)
- `REACT_APP_API_BASE_URL` - Public API endpoint URLs

**Sensitive Secrets** (must be secured):
- `REACT_APP_GOOGLE_CLIENT_ID` - OAuth client ID (somewhat sensitive, but visible in browser)

### Secrets Management Options

#### Option 1: Environment File (Development)
```bash
# Use .env file (already configured in docker-compose.yml)
docker-compose up
```

#### Option 2: Docker Secrets (Production - Docker Swarm)
```bash
# Create secrets
echo "your-client-id.apps.googleusercontent.com" | docker secret create google_oauth_client_id -

# Deploy with secrets
docker stack deploy -c docker-compose.swarm.yml contacts
```

#### Option 3: External Secret Managers (Enterprise)
- **HashiCorp Vault**: Centralized secret management
- **AWS Secrets Manager**: Cloud-native secret storage
- **Kubernetes Secrets**: Container orchestration secrets

## Building the Docker Image

### Standard Build

```bash
docker build -t contacts-app:latest .
```

### Build with Specific Version Tag

```bash
docker build -t contacts-app:1.0.0 .
```

### Build Arguments (if needed)

```bash
docker build \
  --build-arg NODE_VERSION=18.20.4 \
  -t contacts-app:latest \
  .
```

## Running with Docker Compose

### Start the Application

```bash
# Start in detached mode
docker-compose up -d

# View logs
docker-compose logs -f contacts-app

# Stop the application
docker-compose down
```

### Access the Application

- **HTTP**: http://localhost
- **Health Check**: http://localhost/health

## Security Features

### Docker Security Hardening

The deployment implements comprehensive security measures:

#### 1. **Non-Root User Execution** (CIS 4.1, 5.10)
- Build stage: runs as `nodejs` user (UID 1001)
- Runtime stage: runs as `nginx` user (UID 101)
- **Verification**:
  ```bash
  docker exec contacts-app whoami  # Should output: nginx
  ```

#### 2. **Read-Only Root Filesystem** (CIS 5.12)
- Container filesystem is read-only
- Temporary directories mounted as tmpfs
- **Verification**:
  ```bash
  docker exec contacts-app touch /test  # Should fail
  docker exec contacts-app touch /tmp/test  # Should succeed
  ```

#### 3. **Linux Capabilities** (CIS 5.25)
- All capabilities dropped by default
- Only essential capabilities added:
  - `CHOWN`: File ownership changes
  - `SETGID`/`SETUID`: User switching
  - `NET_BIND_SERVICE`: Port binding (8080)

#### 4. **Resource Limits** (CIS 5.10)
- CPU: 1.0 core maximum, 0.25 core reserved
- Memory: 512MB maximum, 128MB reserved
- Prevents resource exhaustion attacks

#### 5. **Security Options**
- `no-new-privileges:true`: Prevents privilege escalation

### Nginx Security Headers

The application serves the following security headers:

- **X-Frame-Options**: `SAMEORIGIN` - Prevents clickjacking
- **X-Content-Type-Options**: `nosniff` - Prevents MIME sniffing
- **X-XSS-Protection**: `1; mode=block` - Legacy XSS protection
- **Referrer-Policy**: `strict-origin-when-cross-origin` - Controls referrer information
- **Content-Security-Policy**: Restrictive CSP for XSS prevention
- **Permissions-Policy**: Disables unnecessary browser features

### Port Configuration

- **Container Port**: 8080 (non-privileged, allows non-root nginx)
- **Host Port**: 80 (mapped in docker-compose.yml)
- **Rationale**: Non-root users cannot bind to ports < 1024

## Health Monitoring

### Docker Health Check

The container includes a built-in health check:

```bash
# Check container health status
docker ps --format "table {{.Names}}\t{{.Status}}"

# Manual health check
curl http://localhost/health
```

### Health Check Configuration

- **Interval**: 30 seconds
- **Timeout**: 3 seconds
- **Start Period**: 5 seconds (grace period)
- **Retries**: 3 attempts before marking unhealthy

## Security Validation

### Image Vulnerability Scanning

```bash
# Install trivy
brew install aquasecurity/trivy/trivy  # macOS
# or download from https://github.com/aquasecurity/trivy/releases

# Scan for vulnerabilities
trivy image contacts-app:latest

# Scan for HIGH and CRITICAL only
trivy image --severity HIGH,CRITICAL contacts-app:latest

# Scan for secrets
trivy image --scanners secret contacts-app:latest

# Fail build on HIGH/CRITICAL vulnerabilities
trivy image --severity HIGH,CRITICAL --exit-code 1 contacts-app:latest
```

### Security Audit Checklist

```bash
# ✅ Verify non-root user
docker inspect contacts-app | grep -i user

# ✅ Verify read-only filesystem
docker inspect contacts-app | grep -i readonly

# ✅ Verify no secrets in image history
docker history contacts-app --no-trunc | grep -iE "secret|password|key|token"

# ✅ Verify security options
docker inspect contacts-app | grep -i securityopt

# ✅ Verify resource limits
docker inspect contacts-app | grep -iE "memory|cpu"
```

## Troubleshooting

### Build Failures

**Issue**: TypeScript errors during build
**Solution**: See [Known Issues](#known-issues) section above

**Issue**: Permission denied errors
**Solution**: Ensure Docker daemon is running and user has permissions

### Runtime Issues

**Issue**: Container exits immediately
**Solution**: Check logs with `docker-compose logs contacts-app`

**Issue**: Health check failing
**Solution**: Verify nginx is listening on port 8080 inside container

**Issue**: OAuth not working
**Solution**: Verify `REACT_APP_GOOGLE_CLIENT_ID` is set correctly in `.env`

### Port Conflicts

**Issue**: Port 80 already in use
**Solution**: Modify `docker-compose.yml` to use different host port:
```yaml
ports:
  - "8080:8080"  # Instead of "80:8080"
```

## Production Deployment Checklist

### Pre-Deployment

- [ ] All environment variables configured in `.env`
- [ ] Secrets managed securely (not committed to git)
- [ ] Image scanned for vulnerabilities with trivy
- [ ] Health check endpoint tested and responding
- [ ] Resource limits appropriate for production load
- [ ] SSL/TLS certificates configured (if using HTTPS)

### Deployment

- [ ] Build image with version tag (e.g., `1.0.0`)
- [ ] Push image to container registry (Docker Hub, ECR, GCR)
- [ ] Deploy using docker-compose or orchestration platform
- [ ] Verify health check status
- [ ] Test application functionality
- [ ] Monitor logs for errors

### Post-Deployment

- [ ] Set up log aggregation (ELK, CloudWatch, etc.)
- [ ] Configure monitoring and alerting
- [ ] Document rollback procedure
- [ ] Schedule regular vulnerability scans
- [ ] Review and rotate secrets periodically

## Maintenance

### Updating the Application

```bash
# Pull latest code
git pull origin main

# Rebuild image
docker-compose build

# Restart with new image
docker-compose down && docker-compose up -d
```

### Log Management

```bash
# View real-time logs
docker-compose logs -f contacts-app

# View last 100 lines
docker-compose logs --tail=100 contacts-app

# Save logs to file
docker-compose logs contacts-app > app-logs.txt
```

### Backup and Restore

Currently, the application uses Google Contacts as the source of truth. No local database backup is required. Ensure:

- OAuth credentials are backed up securely
- Docker Compose configuration is version controlled
- Environment variable documentation is maintained

## Additional Resources

- [CIS Docker Benchmark](https://www.cisecurity.org/benchmark/docker)
- [OWASP Docker Security Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Docker_Security_Cheat_Sheet.html)
- [Docker Security Best Practices](https://docs.docker.com/develop/security-best-practices/)
- [Google OAuth 2.0 Documentation](https://developers.google.com/identity/protocols/oauth2)

## Support

For issues or questions:
1. Check this documentation
2. Review application logs
3. Consult project README.md
4. Contact the development team
