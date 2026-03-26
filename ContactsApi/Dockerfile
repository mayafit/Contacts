# syntax=docker/dockerfile:1
# Multi-stage Dockerfile for ContactsApi backend
# Compliant with CIS Docker Benchmark v1.6.0
# Story: 2-1-backend-api
# Supports cross-compilation: build on amd64, target arm64

###############################
# Stage 1: Build (runs on build host's native arch)
###############################
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build

# Resolve dotnet RID from Docker TARGETARCH (amd64→x64, arm64→arm64)
ARG TARGETARCH
RUN if [ "$TARGETARCH" = "arm64" ]; then echo "linux-musl-arm64" > /tmp/rid; \
    else echo "linux-musl-x64" > /tmp/rid; fi

# Create non-root user for build (CIS 4.1)
RUN addgroup -g 1001 -S dotnet && \
    adduser -S dotnet -u 1001 -G dotnet

WORKDIR /src

# Copy solution and project files
COPY --chown=dotnet:dotnet ContactsApi.sln ./
COPY --chown=dotnet:dotnet src/ContactsApi.WebApi/ContactsApi.WebApi.csproj ./src/ContactsApi.WebApi/
COPY --chown=dotnet:dotnet src/ContactsApi.Core/ContactsApi.Core.csproj ./src/ContactsApi.Core/
COPY --chown=dotnet:dotnet src/ContactsApi.Infrastructure/ContactsApi.Infrastructure.csproj ./src/ContactsApi.Infrastructure/

# Restore dependencies as non-root user
USER dotnet
RUN dotnet restore --runtime $(cat /tmp/rid)

# Copy remaining source code
COPY --chown=dotnet:dotnet . .

# Create publish directory with proper permissions
USER root
RUN mkdir -p /app/publish && chown -R dotnet:dotnet /app
USER dotnet

# Build and publish (cross-compiles natively — no QEMU needed)
WORKDIR /src/src/ContactsApi.WebApi
RUN dotnet publish -c Release -o /app/publish \
    --runtime $(cat /tmp/rid) \
    --self-contained false

###############################
# Stage 2: Runtime (runs on target arch)
###############################
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine

# Add metadata labels (CIS 4.3)
LABEL maintainer="contacts-team" \
      version="1.0.0" \
      description="ContactsApi - C# ASP.NET backend for Google People API integration" \
      org.opencontainers.image.source="https://github.com/mayafit/contacts"

# Create non-root user for runtime (CIS 4.1)
RUN addgroup -g 1001 -S dotnet && \
    adduser -S dotnet -u 1001 -G dotnet

WORKDIR /app

# Create required directories with dotnet user ownership
RUN mkdir -p /app/logs && \
    chown -R dotnet:dotnet /app

# Copy published app from build stage
COPY --from=build --chown=dotnet:dotnet /app/publish ./

# Set file permissions (CIS 5.12)
RUN find /app -type f -exec chmod 444 {} \; && \
    find /app -type d -exec chmod 555 {} \; && \
    chmod 755 /app/logs

# Expose non-privileged port (CIS 5.7)
EXPOSE 5000

# Add healthcheck (CIS 4.6)
HEALTHCHECK --interval=30s \
            --timeout=10s \
            --start-period=40s \
            --retries=3 \
    CMD wget --quiet --tries=1 --spider http://127.0.0.1:5000/health || exit 1

# Switch to non-root user (CIS 4.1, CIS 5.10)
USER dotnet

# Set environment variables
ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

# Run the application
ENTRYPOINT ["dotnet", "ContactsApi.WebApi.dll"]
