FROM node:24.19.0-bookworm-slim AS frontend
WORKDIR /build
COPY src/Fluxus.Web/package*.json ./
RUN npm ci
COPY src/Fluxus.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS backend
WORKDIR /build
COPY global.json Directory.Build.props Fluxus.slnx ./
COPY src/Fluxus.Core/ src/Fluxus.Core/
COPY src/Fluxus.Infrastructure/ src/Fluxus.Infrastructure/
COPY src/Fluxus.Api/ src/Fluxus.Api/
COPY tests/fixtures/ tests/fixtures/
RUN dotnet publish src/Fluxus.Api -c Release -o /publish -p:RestoreLockedMode=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9
WORKDIR /app
COPY --from=backend /publish ./
COPY --from=frontend /build/dist/fluxus-web/browser/ ./wwwroot/
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Urls=http://0.0.0.0:8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Fluxus.Api.dll"]
