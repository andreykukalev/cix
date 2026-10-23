# CIX engine: build with the SDK, run on the ASP.NET runtime as a non-root user. No secrets are baked in:
# they arrive at run time as files under /run/secrets (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY config/ config/
COPY strategies/ strategies/
COPY src/ src/
RUN dotnet publish src/Cix.Engine/Cix.Engine.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# The tool server binds 127.0.0.1 only (ToolServer:Url via ListenOnLoopback). Clearing the image's default 8080
# setting just avoids a startup warning about overridden addresses.
ENV ASPNETCORE_HTTP_PORTS= \
    DOTNET_NOLOGO=1
USER $APP_UID
ENTRYPOINT ["dotnet", "Cix.Engine.dll"]
