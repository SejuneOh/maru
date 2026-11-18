# API image. Build from the repository root (the migrations in db/ are embedded into the assembly):
#   docker build -f deploy/api/Dockerfile .
# Migrate: run the same image with the argument `migrate`.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY server/src/WorkBoard.Api/WorkBoard.Api.csproj server/src/WorkBoard.Api/
RUN dotnet restore server/src/WorkBoard.Api/WorkBoard.Api.csproj
COPY server/src/WorkBoard.Api/ server/src/WorkBoard.Api/
COPY db/ db/
RUN dotnet publish server/src/WorkBoard.Api/WorkBoard.Api.csproj -c Release -o /app --no-restore \
    && rm -f /app/appsettings.Development.json /app/appsettings.Local*.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Npgsql probes GSSAPI encryption on connect and logs an error when the library is missing.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000
USER $APP_UID
ENTRYPOINT ["dotnet", "WorkBoard.Api.dll"]
