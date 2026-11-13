# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PlanGuard.Core/PlanGuard.Core.csproj src/PlanGuard.Core/
COPY src/PlanGuard.Postgres/PlanGuard.Postgres.csproj src/PlanGuard.Postgres/
COPY src/PlanGuard.Cli/PlanGuard.Cli.csproj src/PlanGuard.Cli/
COPY src/PlanGuard.Lab/PlanGuard.Lab.csproj src/PlanGuard.Lab/
RUN dotnet restore src/PlanGuard.Cli/PlanGuard.Cli.csproj && \
    dotnet restore src/PlanGuard.Lab/PlanGuard.Lab.csproj

COPY .editorconfig README.md ./
COPY samples/ samples/
COPY src/ src/
RUN dotnet publish src/PlanGuard.Cli/PlanGuard.Cli.csproj -c Release --no-restore -o /app/cli -p:UseAppHost=false && \
    dotnet publish src/PlanGuard.Lab/PlanGuard.Lab.csproj -c Release --no-restore -o /app/lab -p:UseAppHost=false && \
    mkdir -p /out

# ---- runtime -----------------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, non-root "app" user (UID 1654).
# One image, two entry points: the plan-guard CLI (default) and the lab (`dotnet /app/lab/plan-guard-lab.dll`).
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.title="plan-guard" \
      org.opencontainers.image.description="CI query-plan regression detector for PostgreSQL" \
      org.opencontainers.image.source="https://github.com/hekimcan206/plan-guard" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/cli /app/cli
COPY --from=build --link /app/lab /app/lab
COPY --from=build --link /src/samples /app/samples
COPY --from=build --chown=1654:1654 /out /out
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/cli/plan-guard.dll"]
CMD ["--help"]
