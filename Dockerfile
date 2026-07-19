# Multi-stage build for CityChecker API + SPA
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/CityChecker.Api/CityChecker.Api.csproj src/CityChecker.Api/
RUN dotnet restore src/CityChecker.Api/CityChecker.Api.csproj
COPY src/CityChecker.Api/ src/CityChecker.Api/
RUN dotnet publish src/CityChecker.Api/CityChecker.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
COPY DataImports /app/DataImports
ENTRYPOINT ["dotnet", "CityChecker.Api.dll"]
