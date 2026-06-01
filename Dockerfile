FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY AvtoXabarchiBot.slnx ./
COPY AvtoXabarchiBot.Core/AvtoXabarchiBot.Core.csproj AvtoXabarchiBot.Core/
COPY AvtoXabarchiBot.Infrastructure/AvtoXabarchiBot.Infrastructure.csproj AvtoXabarchiBot.Infrastructure/
COPY AvtoXabarchiBot.Web/AvtoXabarchiBot.Web.csproj AvtoXabarchiBot.Web/

# Restore dependencies
RUN dotnet restore AvtoXabarchiBot.Web/AvtoXabarchiBot.Web.csproj

# Copy everything else and build
COPY . .
RUN dotnet publish AvtoXabarchiBot.Web/AvtoXabarchiBot.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080}

ENTRYPOINT ["dotnet", "AvtoXabarchiBot.Web.dll"]
