# The stocks advisor's daily run (ADR-0023): build with the SDK, run on the .NET runtime as a non-root user. Built from the
# repository root (`docker build -f stocks/Dockerfile .`): it needs Cix.Domain and the shared build files, never the engine.
# No secrets are baked in: the EODHD key arrives at run time as /run/secrets/eodhd-api-token, from Key Vault.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Cix.Domain/ src/Cix.Domain/
COPY stocks/src/ stocks/src/
COPY stocks/config/ stocks/config/
RUN dotnet publish stocks/src/Cix.Stocks.Host/Cix.Stocks.Host.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
ENV DOTNET_NOLOGO=1
USER $APP_UID
ENTRYPOINT ["dotnet", "Cix.Stocks.Host.dll"]
