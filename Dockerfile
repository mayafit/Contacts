# syntax=docker/dockerfile:1
# Multi-stage hardened Dockerfile for Contacts application
# Compliant with CIS Docker Benchmark v1.6.0 and OWASP Docker Security
# Story: 1-7-set-up-docker-deployment-configuration

###############################
# Stage 1: Builder
###############################
FROM node:18.20.4-alpine3.20 AS builder

# Build arguments for React environment variables
# These are baked into the bundle at build time
ARG REACT_APP_GOOGLE_CLIENT_ID
ARG REACT_APP_API_BASE_URL=http://localhost:5000/api

# Set as environment variables for the build process
ENV REACT_APP_GOOGLE_CLIENT_ID=${REACT_APP_GOOGLE_CLIENT_ID}
ENV REACT_APP_API_BASE_URL=${REACT_APP_API_BASE_URL}

# Create non-root user for build stage (CIS 4.1)
RUN addgroup -g 1001 -S nodejs && \
    adduser -S nodejs -u 1001 -G nodejs

# Set working directory and change ownership
WORKDIR /app
RUN chown -R nodejs:nodejs /app

# Copy dependency files with correct ownership
COPY --chown=nodejs:nodejs package*.json ./

# Install dependencies as non-root user
# Use npm ci for reproducible builds (CIS 4.5)
# Clean cache to reduce image size
USER nodejs
RUN npm ci && \
    npm cache clean --force

# Copy application source with correct ownership
COPY --chown=nodejs:nodejs . .

# Build the application
RUN npm run build:prod

###############################
# Stage 2: Production Runtime
###############################
FROM nginx:1.27.3-alpine3.20

# Add metadata labels (CIS 4.3)
LABEL maintainer="contacts-team" \
      version="1.0.0" \
      description="Contacts application with hardened security configuration" \
      org.opencontainers.image.source="https://github.com/mayafit/contacts"

# Create required directories with nginx user ownership
# nginx user already exists in nginx:alpine image (UID/GID 101)
# Note: These directories will be overridden by tmpfs in docker-compose,
# but we create them here to set ownership for when tmpfs mounts
RUN touch /var/run/nginx.pid && \
    mkdir -p /var/cache/nginx \
             /var/log/nginx && \
    chown -R nginx:nginx /var/cache/nginx \
                         /var/log/nginx \
                         /var/run/nginx.pid && \
    chmod -R 777 /var/cache/nginx /var/run && \
    chmod 755 /var/log/nginx

# Remove default nginx content and unnecessary packages (attack surface reduction)
RUN rm -rf /usr/share/nginx/html/* && \
    rm -f /etc/nginx/conf.d/default.conf

# Set working directory
WORKDIR /usr/share/nginx/html

# Copy build artifacts from builder stage with nginx ownership (CIS 4.1)
COPY --from=builder --chown=nginx:nginx /app/dist ./

# Copy nginx configuration files with correct ownership
COPY --chown=nginx:nginx .nginx/nginx.conf /etc/nginx/nginx.conf
COPY --chown=nginx:nginx .nginx/mime.types /etc/nginx/mime.types

# Set read-only permissions on static files (CIS 5.12)
# Directories need execute permission for traversal
RUN find /usr/share/nginx/html -type f -exec chmod 444 {} \; && \
    find /usr/share/nginx/html -type d -exec chmod 555 {} \; && \
    chmod 444 /etc/nginx/nginx.conf /etc/nginx/mime.types

# Expose non-privileged ports (CIS 5.7)
# Using 8080 instead of 80 since nginx runs as non-root
EXPOSE 8080

# Add healthcheck (CIS 4.6)
HEALTHCHECK --interval=30s \
            --timeout=10s \
            --start-period=40s \
            --retries=3 \
    CMD wget --quiet --tries=1 --spider http://127.0.0.1:8080/health || exit 1

# Switch to non-root user (CIS 4.1, CIS 5.10)
USER nginx

# Run nginx in foreground
CMD ["nginx", "-g", "daemon off;"]
