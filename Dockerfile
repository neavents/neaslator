FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG GITHUB_USER
ARG GITHUB_TOKEN
WORKDIR /src

COPY neaslator/Directory.Packages.props neaslator/
COPY neaslator/Directory.Build.props neaslator/
COPY neaslator/nuget.config neaslator/

# nuget.config declares the GitHub Packages source but carries no credentials (they must not be
# committed). Inject them here, as the other services' Dockerfiles do — without this, restoring
# Neavents.Messaging.Contracts fails with NU1301 / 401 Unauthorized.
#
# This replaced a ProjectReference into ../neavents-messaging-contracts, which is why this image
# used to copy that repo's Directory.Build.props and csproj in just to give restore a
# TargetFramework to evaluate.
RUN dotnet nuget update source GitHub \
    --username "${GITHUB_USER}" \
    --password "${GITHUB_TOKEN}" \
    --store-password-in-clear-text \
    --configfile neaslator/nuget.config

COPY neaslator/src/Neaslator/Neaslator.csproj neaslator/src/Neaslator/

RUN dotnet restore neaslator/src/Neaslator/Neaslator.csproj

COPY neaslator/src/Neaslator/ neaslator/src/Neaslator/

RUN dotnet publish neaslator/src/Neaslator/Neaslator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# curl is here for the HEALTHCHECK below — this image ships no HTTP client, so Docker had no way
# to probe the container and reported no health status at all.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .
EXPOSE 5300

# /health is exempt from InternalKeyMiddleware for exactly this reason: the probe carries no
# gateway secret, so gating it would report the container permanently unhealthy.
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:5300/health || exit 1

ENTRYPOINT ["dotnet", "Neaslator.dll"]
