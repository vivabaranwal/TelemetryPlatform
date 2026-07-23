# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy the full TelemetryPlatform subtree (this Dockerfile lives at its root)
COPY . .

# Publish the API in Release mode to /app/publish
RUN dotnet publish src/TelemetryPlatform.Api/TelemetryPlatform.Api.csproj \
    -c Release -o /app/publish

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

COPY --from=build /app/publish .

# Railway injects PORT automatically; ASP.NET Core reads ASPNETCORE_URLS or PORT.
EXPOSE 8080

ENTRYPOINT ["dotnet", "TelemetryPlatform.Api.dll"]
