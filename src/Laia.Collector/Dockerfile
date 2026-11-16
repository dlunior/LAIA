# LAIA Collector: built from the repository root (docker build -f src/Laia.Collector/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Laia.Common/ src/Laia.Common/
COPY src/Laia.Server/TrafficCore/ src/Laia.Server/TrafficCore/
COPY src/Laia.Collector/ src/Laia.Collector/
ARG VERSION=1.0.0
RUN dotnet publish src/Laia.Collector/Laia.Collector.csproj -c Release -o /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV LAIA_DATA=/data \
    ASPNETCORE_URLS=http://+:47900
VOLUME /data
EXPOSE 47900
ENTRYPOINT ["dotnet", "Laia.Collector.dll"]
