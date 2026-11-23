# PowerBase.ComputeWorker for Azure Container Apps. Build from the repository root:
#   docker build -f src/PowerBase.ComputeWorker/Dockerfile -t powerbase-compute .
# Secrets come from Key Vault (KeyVault__Uri) / managed identity, never from the image.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/PowerBase.ComputeWorker/PowerBase.ComputeWorker.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "PowerBase.ComputeWorker.dll"]
