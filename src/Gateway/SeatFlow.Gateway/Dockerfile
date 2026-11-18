# syntax=docker/dockerfile:1
# Build context: repository root (needs Directory.*.props and the building blocks).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked     dotnet publish src/Gateway/SeatFlow.Gateway/SeatFlow.Gateway.csproj -c Release -o /app -p:UseAppHost=false

# Chiseled: no shell, no package manager, non-root by default. The HEALTHCHECK uses the app itself.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
HEALTHCHECK --interval=5s --timeout=5s --start-period=15s --retries=20 CMD ["dotnet", "SeatFlow.Gateway.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "SeatFlow.Gateway.dll"]
