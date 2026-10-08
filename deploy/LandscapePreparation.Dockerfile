FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src
COPY global.json Fluxus.slnx Directory.Build.props ./
COPY src/Fluxus.Core/ src/Fluxus.Core/
COPY src/Fluxus.Infrastructure/ src/Fluxus.Infrastructure/
COPY src/Fluxus.Landscape.Worker/ src/Fluxus.Landscape.Worker/
RUN dotnet publish src/Fluxus.Landscape.Worker -c Release -o /app -p:RestoreLockedMode=true

FROM mcr.microsoft.com/dotnet/runtime:10.0.9-noble AS runtime
# Inspect actual executable versions: this upstream tag reports PDAL 2.8.4 / GDAL 3.11.3.
FROM pdal/pdal:2.9.2@sha256:18d73101500cd673052ee4ead8a4b716a9dad09c8711e88e7a37abed2496c469
COPY --from=runtime /usr/share/dotnet /usr/share/dotnet
COPY --from=runtime /usr/lib/x86_64-linux-gnu/libicu*.so* /usr/lib/x86_64-linux-gnu/
ENV DOTNET_ROOT=/usr/share/dotnet
COPY --from=build /app /app
WORKDIR /app
ENTRYPOINT ["/usr/share/dotnet/dotnet", "Fluxus.Landscape.Worker.dll"]
