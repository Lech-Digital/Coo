# Coo API: multi-stage build for the Hetzner host (Spec 008 §7). Built on the server from the rsynced source,
# as PurePrep and CoreChoice are. global.json is not copied: it pins the Mac's SDK, and the image brings its own.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY NuGet.config Directory.Build.props Directory.Packages.props BannedSymbols.txt .editorconfig ./
COPY eng/banned-non-host/ eng/banned-non-host/
COPY src/Shared/ src/Shared/
COPY src/Backend/ src/Backend/
RUN dotnet restore src/Backend/Coo.Api/Coo.Api.csproj
# The commit goes into the version ("0.8.0+<commit>"), which /health reports and the deploy script checks.
ARG GIT_SHA=unknown
RUN dotnet publish src/Backend/Coo.Api/Coo.Api.csproj -c Release -o /app --no-restore -p:SourceRevisionId=$GIT_SHA

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# The image's non-root user. Secrets (the connection string) come from the compose file at runtime.
USER $APP_UID
ENTRYPOINT ["dotnet", "Coo.Api.dll"]
