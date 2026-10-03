# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY FlightWall.sln ./
COPY src/FlightWall.Core/*.csproj src/FlightWall.Core/
COPY src/FlightWall.Infrastructure/*.csproj src/FlightWall.Infrastructure/
COPY src/FlightWall.Web/*.csproj src/FlightWall.Web/
RUN dotnet restore src/FlightWall.Web/FlightWall.Web.csproj

COPY src/ src/
RUN dotnet publish src/FlightWall.Web/FlightWall.Web.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Default="Data Source=/data/flightwall.db" \
    FlightWall__LogoCacheDir=/data/logos \
    DOTNET_RUNNING_IN_CONTAINER=true
RUN mkdir -p /data && chown -R $APP_UID /data
COPY --from=build /app .
USER $APP_UID
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "FlightWall.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "FlightWall.Web.dll"]
