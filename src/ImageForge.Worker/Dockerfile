# syntax=docker/dockerfile:1.6
# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/ImageForge.Shared/ImageForge.Shared.csproj   src/ImageForge.Shared/
COPY src/ImageForge.Worker/ImageForge.Worker.csproj   src/ImageForge.Worker/
COPY src/ImageForge.Api/ImageForge.Api.csproj         src/ImageForge.Api/
COPY global.json                                      ./
RUN dotnet restore src/ImageForge.Worker/ImageForge.Worker.csproj

COPY src/ ./src/

RUN dotnet publish src/ImageForge.Worker/ImageForge.Worker.csproj \
        --configuration Release \
        --output /app/publish \
        --no-restore

# ---- runtime stage ----
# The worker is not an HTTP server, so we use the smaller generic
# .NET runtime image (no ASP.NET stack) — fewer megabytes per copy.
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "ImageForge.Worker.dll"]
