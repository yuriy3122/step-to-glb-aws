# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS frontend
WORKDIR /frontend
COPY src/frontend/package*.json ./
RUN npm ci
COPY src/frontend ./
RUN npm run build && rm dist/config.json

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG PROJECT=JobApi
WORKDIR /source
COPY Directory.Build.props global.json ./
COPY src/backend ./src/backend
RUN dotnet restore src/backend/${PROJECT}/${PROJECT}.csproj --locked-mode --disable-parallel
RUN dotnet publish src/backend/${PROJECT}/${PROJECT}.csproj -c Release --no-restore -o /app -m:1 /p:UseAppHost=false
RUN --mount=from=frontend,source=/frontend/dist,target=/frontend \
    if [ "$PROJECT" = JobApi ]; then mkdir -p /app/wwwroot && cp -a /frontend/. /app/wwwroot; fi
RUN printf 'dotnet %s.dll\n' "${PROJECT}" > /app/start-command
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
USER 1654
EXPOSE 8080
ENTRYPOINT ["/bin/sh", "-c", "exec $(cat /app/start-command)"]
