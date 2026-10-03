# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY PlaneWeb.sln ./
COPY src/PlaneWeb.Core/*.csproj src/PlaneWeb.Core/
COPY src/PlaneWeb.Infrastructure/*.csproj src/PlaneWeb.Infrastructure/
COPY src/PlaneWeb.Web/*.csproj src/PlaneWeb.Web/
# Restore against project files first for layer caching; publish below restores again with full sources
# so framework static assets (e.g. blazor.web.js) are included. Do not add --no-restore to publish.
RUN dotnet restore src/PlaneWeb.Web/PlaneWeb.Web.csproj

COPY src/ src/
RUN dotnet publish src/PlaneWeb.Web/PlaneWeb.Web.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Default="Data Source=/data/planeweb.db" \
    PlaneWeb__LogoCacheDir=/data/logos \
    PlaneWeb__TrailsFile=/data/trails.json \
    PlaneWeb__KeysDir=/data/keys \
    DOTNET_RUNNING_IN_CONTAINER=true
RUN mkdir -p /data && chown -R $APP_UID /data
COPY --from=build /app .
USER $APP_UID
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "PlaneWeb.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "PlaneWeb.Web.dll"]
