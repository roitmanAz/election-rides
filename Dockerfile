# One image for hosting: the screens (Angular) are built and served by the API from wwwroot.
# Build: docker build -t election-rides .

# 1. Screens
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npx ng build --configuration production

# 2. API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY api/src/ElectionRides.Api/ElectionRides.Api.csproj api/src/ElectionRides.Api/
RUN dotnet restore api/src/ElectionRides.Api/ElectionRides.Api.csproj
COPY api/src/ api/src/
RUN dotnet publish api/src/ElectionRides.Api/ElectionRides.Api.csproj -c Release -o /app --no-restore

# 3. Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/dist/web/browser ./wwwroot
ENV ASPNETCORE_HTTP_PORTS=10000 \
    Database__MigrateOnStart=true
EXPOSE 10000
USER $APP_UID
ENTRYPOINT ["dotnet", "ElectionRides.Api.dll"]
