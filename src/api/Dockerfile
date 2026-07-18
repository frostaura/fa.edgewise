# Edgewise API image. Build context is the REPO ROOT:
#   docker build -f src/api/Dockerfile .
# Multi-stage: sdk builds/publishes, aspnet runs as non-root with /data for attachments.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first so `dotnet restore` layers cache until dependencies change.
COPY src/api/Edgewise.Api/Edgewise.Api.csproj src/api/Edgewise.Api/
COPY src/api/Edgewise.Domain/Edgewise.Domain.csproj src/api/Edgewise.Domain/
COPY src/api/Edgewise.Infrastructure/Edgewise.Infrastructure.csproj src/api/Edgewise.Infrastructure/
COPY src/api/Edgewise.Contracts/Edgewise.Contracts.csproj src/api/Edgewise.Contracts/
RUN dotnet restore src/api/Edgewise.Api/Edgewise.Api.csproj

# Copy the rest of the API source and publish.
COPY src/api/ src/api/
RUN dotnet publish src/api/Edgewise.Api/Edgewise.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl for the container healthcheck.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Non-root user pinned to uid/gid 64198 (repurpose the base image's `app` user
# if present, create it otherwise) and the attachment storage directory.
RUN if id app >/dev/null 2>&1; then \
        groupmod --gid 64198 app && usermod --uid 64198 --gid 64198 app; \
    else \
        groupadd --gid 64198 app && useradd --uid 64198 --gid 64198 --create-home app; \
    fi \
    && mkdir -p /data \
    && chown -R app:app /data /app

COPY --from=build --chown=app:app /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Edgewise.Api.dll"]
