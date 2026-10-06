FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /build
COPY global.json Directory.Build.props ./
COPY src/Fluxus.Core/ src/Fluxus.Core/
COPY src/Fluxus.Infrastructure/ src/Fluxus.Infrastructure/
COPY src/Fluxus.Landscape.Worker/ src/Fluxus.Landscape.Worker/
RUN dotnet publish src/Fluxus.Landscape.Worker -c Release -o /publish -p:RestoreLockedMode=true

FROM mcr.microsoft.com/dotnet/runtime:10.0.9-noble AS dotnet
# Official OSGeo image, pinned to the version tested with below-canopy observation heights.
FROM ghcr.io/osgeo/gdal:ubuntu-small-3.11.4@sha256:4b94b5e76e8b5616a4d5dc38e6e20def10ab70c3487eeb923d90547372e70a03
COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH=/usr/share/dotnet:$PATH
WORKDIR /app
COPY --from=build /publish ./
ENTRYPOINT ["dotnet", "Fluxus.Landscape.Worker.dll"]
CMD ["--watch"]
