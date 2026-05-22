# syntax=docker/dockerfile:1.6
# ---- build stage ----
# Use the .NET 8 SDK image to compile and publish. Layer the .csproj
# files first so a code-only change can reuse the dotnet restore cache.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/ImageForge.Shared/ImageForge.Shared.csproj   src/ImageForge.Shared/
COPY src/ImageForge.Api/ImageForge.Api.csproj         src/ImageForge.Api/
COPY src/ImageForge.Worker/ImageForge.Worker.csproj   src/ImageForge.Worker/
COPY global.json                                      ./
RUN dotnet restore src/ImageForge.Api/ImageForge.Api.csproj

COPY src/      ./src/
COPY frontend/ ./frontend/

RUN dotnet publish src/ImageForge.Api/ImageForge.Api.csproj \
        --configuration Release \
        --output /app/publish \
        --no-restore

# ---- runtime stage ----
# Slim ASP.NET runtime; no SDK, no source, no tooling. The frontend
# files travel into the image so the API can serve them as static
# assets without a separate web server.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./
COPY --from=build /src/frontend ./frontend

# Listen on 8080 in the container; docker-compose maps the host port.
ENV ASPNETCORE_URLS=http://+:8080
ENV Frontend__Path=/app/frontend
EXPOSE 8080

ENTRYPOINT ["dotnet", "ImageForge.Api.dll"]
