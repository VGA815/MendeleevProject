# Build context: repository root.
#   docker build -f tools/Mendeleev.LoadTest/Dockerfile -t mendeleev-loadtest .
# Runs on the staging VPS as the `loadtest` service of deploy/docker-compose.staging.yml (docs/staging.md).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["tools/Mendeleev.LoadTest/Mendeleev.LoadTest.csproj", "tools/Mendeleev.LoadTest/"]
RUN dotnet restore "tools/Mendeleev.LoadTest/Mendeleev.LoadTest.csproj"
COPY tools/Mendeleev.LoadTest/ tools/Mendeleev.LoadTest/
RUN dotnet publish "tools/Mendeleev.LoadTest/Mendeleev.LoadTest.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8081
USER $APP_UID
ENTRYPOINT ["dotnet", "Mendeleev.LoadTest.dll"]
