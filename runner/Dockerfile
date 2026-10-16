# Factory runner image (M9.2): .NET SDK + Node with the starter template's NuGet and npm packages
# cached, so verification steps run with the network disabled. Build context: templates/starter-v1.
# Base images are pinned by digest; see scripts/Build-FactoryRunner.ps1.
FROM node@sha256:a5f4366e834695d5f363b920c104953a535d3a9864d1bdf74566a0ebe01daee5 AS node

FROM mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/npm
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
 && useradd --uid 10001 --user-group --no-create-home --home-dir /tmp runner

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
    NUGET_PACKAGES=/opt/factory/nuget \
    npm_config_cache=/opt/factory/npm \
    npm_config_update_notifier=false \
    npm_config_fund=false \
    npm_config_audit=false \
    NG_CLI_ANALYTICS=false \
    HOME=/tmp \
    DOTNET_CLI_HOME=/tmp

# Warm the package caches from the template (trusted, Factory-authored), then drop the sources.
COPY . /opt/factory/seed/
RUN dotnet restore /opt/factory/seed/FactoryStarter.slnx \
 && cd /opt/factory/seed/web && npm ci --ignore-scripts \
 && rm -rf /opt/factory/seed /tmp/* /tmp/.[!.]* /root/.nuget /root/.local \
 && chmod -R a+rX,go-w /opt/factory

USER 10001:10001
WORKDIR /work
