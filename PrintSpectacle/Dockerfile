FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["PrintSpectacle/PrintSpectacle.csproj", "PrintSpectacle/"]
RUN dotnet restore "./PrintSpectacle/PrintSpectacle.csproj"
COPY . .
WORKDIR "/src/PrintSpectacle"
RUN dotnet build "./PrintSpectacle.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./PrintSpectacle.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

EXPOSE 8080

VOLUME ["/data"]
ENTRYPOINT ["dotnet", "PrintSpectacle.dll"]
