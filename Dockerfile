# Build context is the repository root:  docker build -t energy-monitor .

# 1. The PWA. Vite writes into src/EnergyMonitor.Web/wwwroot, which the web host serves.
FROM node:24-alpine AS frontend
WORKDIR /repo/src/frontend
COPY src/frontend/package.json src/frontend/package-lock.json ./
RUN npm ci
COPY src/frontend/ ./
RUN npm run build

# 2. The .NET host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json NuGet.config Directory.Build.props Directory.Packages.props ./
COPY src/EnergyMonitor.Core/EnergyMonitor.Core.csproj src/EnergyMonitor.Core/
COPY src/EnergyMonitor.ServiceDefaults/EnergyMonitor.ServiceDefaults.csproj src/EnergyMonitor.ServiceDefaults/
COPY src/EnergyMonitor.Web/EnergyMonitor.Web.csproj src/EnergyMonitor.Web/
RUN dotnet restore src/EnergyMonitor.Web/EnergyMonitor.Web.csproj
COPY src/EnergyMonitor.Core/ src/EnergyMonitor.Core/
COPY src/EnergyMonitor.ServiceDefaults/ src/EnergyMonitor.ServiceDefaults/
COPY src/EnergyMonitor.Web/ src/EnergyMonitor.Web/
COPY --from=frontend /repo/src/EnergyMonitor.Web/wwwroot/ src/EnergyMonitor.Web/wwwroot/
RUN dotnet publish src/EnergyMonitor.Web/EnergyMonitor.Web.csproj -c Release -o /app --no-restore

# 3. Runtime. The app needs time zone data (Europe/Copenhagen) and runs as the image's non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends tzdata && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "EnergyMonitor.Web.dll"]
