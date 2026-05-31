FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Shared/Shared.csproj src/Shared/
COPY src/ExternalService/ExternalService.csproj src/ExternalService/
RUN dotnet restore src/ExternalService/ExternalService.csproj

COPY src/Shared/ src/Shared/
COPY src/ExternalService/ src/ExternalService/
RUN dotnet publish src/ExternalService/ExternalService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8090
ENTRYPOINT ["dotnet", "ExternalService.dll"]
