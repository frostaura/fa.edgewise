# Edgewise web (Vite/React SPA) image. Build context is src/web:
#   docker build src/web
# Multi-stage: node builds the static bundle, nginx serves it and proxies /api + /mcp.

# ---------- build ----------
FROM node:22-alpine AS build
WORKDIR /app

# Install dependencies first for layer caching.
COPY package.json package-lock.json ./
RUN npm ci

# NOTE: CI builds from a clean checkout, so no node_modules exists in the context.
# For local builds, add a src/web/.dockerignore (node_modules, dist) to keep the
# context clean — this COPY runs after `npm ci` and would overlay node_modules.
COPY . .
RUN npm run build

# ---------- runtime ----------
FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -q --spider http://localhost:80/ || exit 1
