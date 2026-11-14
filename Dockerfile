# syntax=docker/dockerfile:1
# Quaymaster (.NET) image. Build:  docker build -t quaymaster-net:dev .
#
# Socket access without root: the container runs as uid 10001 and is given the host's
# docker group at run time (compose: `group_add: ["${DOCKER_GID}"]`, plain docker:
# `--group-add $(stat -c %g /var/run/docker.sock)`). The image never hard-codes a GID.
# The docker compose CLI is installed in the runtime stage (Compose operations shell
# out to it, same as the original implementation).

ARG NODE_IMAGE=node:22-bookworm-slim
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

# ---- web: build the React frontend -------------------------------------------------------------
FROM ${NODE_IMAGE} AS web
WORKDIR /src
COPY package.json package-lock.json tsconfig.base.json ./
COPY shared/package.json shared/
COPY web/package.json web/
RUN npm ci --no-audit --no-fund
COPY shared shared
COPY web web
RUN npm run build

# ---- dotnet: publish the API -------------------------------------------------------------------
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Quaymaster.Domain/Quaymaster.Domain.csproj src/Quaymaster.Domain/
COPY src/Quaymaster.Application/Quaymaster.Application.csproj src/Quaymaster.Application/
COPY src/Quaymaster.Infrastructure/Quaymaster.Infrastructure.csproj src/Quaymaster.Infrastructure/
COPY src/Quaymaster.Api/Quaymaster.Api.csproj src/Quaymaster.Api/
RUN dotnet restore src/Quaymaster.Api/Quaymaster.Api.csproj
COPY src src
RUN dotnet publish src/Quaymaster.Api/Quaymaster.Api.csproj -c Release -o /app --no-restore

# ---- runtime --------------------------------------------------------------------------------------
FROM ${RUNTIME_IMAGE} AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates tini curl docker.io docker-compose-v2 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd -g 10001 quaymaster \
    && useradd -u 10001 -g quaymaster -m -s /usr/sbin/nologin quaymaster
COPY --from=build /app /app
COPY --from=web /src/web/dist /app/web/dist
ENV QM_WEB_DIR=/app/web/dist
EXPOSE 8787
USER quaymaster
VOLUME ["/var/lib/quaymaster-net"]
ENTRYPOINT ["tini", "--"]
CMD ["dotnet", "/app/Quaymaster.Api.dll"]
