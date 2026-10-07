# Multi-stage build for the WebApp project.
# Used only by docker-compose.full.yml - the default setup runs these on your
# host with `dotnet run`, which is nicer for development.
#
# Build context is the REPO ROOT (see docker-compose.full.yml), so paths below
# are relative to it.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy only the project file first, so `dotnet restore` is cached and does not
# re-run every time a .cs file changes.
COPY src/WebApp/WebApp.csproj src/WebApp/
RUN dotnet restore src/WebApp/WebApp.csproj

COPY src/WebApp/ src/WebApp/
RUN dotnet publish src/WebApp/WebApp.csproj -c Release -o /app --no-restore

# The runtime image has no SDK and no compiler - smaller, and less to attack.
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .

# Run as a non-root user. The aspnet image ships one.
USER $APP_UID

ENTRYPOINT ["dotnet", "WebApp.dll"]
