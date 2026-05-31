FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first for layer caching
COPY src/Shared/Shared.csproj src/Shared/
COPY src/Api/Api.csproj src/Api/
RUN dotnet restore src/Api/Api.csproj

# Copy everything else and publish
COPY src/Shared/ src/Shared/
COPY src/Api/ src/Api/
RUN dotnet publish src/Api/Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Api.dll"]
