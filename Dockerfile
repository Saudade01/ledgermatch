FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Mutabakat.Core/Mutabakat.Core.csproj src/Mutabakat.Core/
COPY src/Mutabakat.Api/Mutabakat.Api.csproj src/Mutabakat.Api/
RUN dotnet restore src/Mutabakat.Api/Mutabakat.Api.csproj
COPY src/ src/
RUN dotnet publish src/Mutabakat.Api/Mutabakat.Api.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mutabakat.Api.dll"]
