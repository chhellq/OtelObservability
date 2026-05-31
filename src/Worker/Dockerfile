FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Shared/Shared.csproj src/Shared/
COPY src/Worker/Worker.csproj src/Worker/
RUN dotnet restore src/Worker/Worker.csproj

COPY src/Shared/ src/Shared/
COPY src/Worker/ src/Worker/
RUN dotnet publish src/Worker/Worker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Worker.dll"]
