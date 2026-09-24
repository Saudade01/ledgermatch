FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/LedgerMatch.Core/LedgerMatch.Core.csproj src/LedgerMatch.Core/
COPY src/LedgerMatch.Api/LedgerMatch.Api.csproj src/LedgerMatch.Api/
RUN dotnet restore src/LedgerMatch.Api/LedgerMatch.Api.csproj
COPY src/ src/
RUN dotnet publish src/LedgerMatch.Api/LedgerMatch.Api.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "LedgerMatch.Api.dll"]
