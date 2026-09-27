# Build context: repository root.
#   docker build -f src/Mendeleev.Web/Dockerfile -t mendeleev-app .                     # service
#   docker build -f src/Mendeleev.Web/Dockerfile --target migrator -t mendeleev-migrator .  # EF migration bundle

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Central Package Management: the props files must be there before restore.
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["src/Mendeleev.SharedKernel/Mendeleev.SharedKernel.csproj", "src/Mendeleev.SharedKernel/"]
COPY ["src/Mendeleev.Domain/Mendeleev.Domain.csproj", "src/Mendeleev.Domain/"]
COPY ["src/Mendeleev.Application/Mendeleev.Application.csproj", "src/Mendeleev.Application/"]
COPY ["src/Mendeleev.Infrastructure/Mendeleev.Infrastructure.csproj", "src/Mendeleev.Infrastructure/"]
COPY ["src/Mendeleev.Web/Mendeleev.Web.csproj", "src/Mendeleev.Web/"]
RUN dotnet restore "src/Mendeleev.Web/Mendeleev.Web.csproj"
COPY src/ src/
RUN dotnet publish "src/Mendeleev.Web/Mendeleev.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM build AS bundle
RUN dotnet tool install --global dotnet-ef --version 10.0.12
ENV PATH="${PATH}:/root/.dotnet/tools"
RUN dotnet ef migrations bundle \
        --project src/Mendeleev.Infrastructure \
        --startup-project src/Mendeleev.Infrastructure \
        --configuration Release \
        --self-contained -r linux-x64 \
        --output /app/efbundle

# Applies migrations and exits. Reads ConnectionStrings__Database (ТЗ 40: миграции — отдельным шагом).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrator
WORKDIR /app
COPY --from=bundle /app/efbundle .
USER $APP_UID
ENTRYPOINT ["/app/efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# curl backs the compose health check; tzdata keeps Europe/Moscow resolvable.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tzdata libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
# The aspnet image listens on 8080 (HTTP_PORTS); polling makes the mounted bot.yaml reload reliably.
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD curl -fsS http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "Mendeleev.Web.dll"]
