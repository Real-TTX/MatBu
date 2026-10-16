FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 9293
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl smbclient \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["MatBu.csproj", "."]
RUN dotnet restore "MatBu.csproj"
COPY . .
RUN dotnet publish "MatBu.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:9293
ENV MATBU_DATA_PATH=/data
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD curl -fsS http://127.0.0.1:9293/api/health || exit 1
ENTRYPOINT ["dotnet", "MatBu.dll"]
