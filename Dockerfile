# Multi-stage build for the Analyst.Api service.
# Stage 1 restores + publishes; stage 2 is a small ASP.NET runtime image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless the project files change).
COPY src/Analyst.Core/Analyst.Core.csproj src/Analyst.Core/
COPY src/Analyst.Api/Analyst.Api.csproj   src/Analyst.Api/
RUN dotnet restore src/Analyst.Api/Analyst.Api.csproj

# Copy the rest and publish (brings in config/schema.fnb.json via the csproj content include).
COPY src/ src/
COPY config/ config/
RUN dotnet publish src/Analyst.Api/Analyst.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Default listen port; overridden by $PORT on hosts that set it.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Analyst.Api.dll"]
