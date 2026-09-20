# VirtualStore.API — .NET 10
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["VirtualStore.API/VirtualStore.API.csproj", "VirtualStore.API/"]
COPY ["VirtualStore.Infrastructure/VirtualStore.Infrastructure.csproj", "VirtualStore.Infrastructure/"]
COPY ["VirtualStore.Application/VirtualStore.Application.csproj", "VirtualStore.Application/"]
COPY ["VirtualStore.Domain/VirtualStore.Domain.csproj", "VirtualStore.Domain/"]
RUN dotnet restore "VirtualStore.API/VirtualStore.API.csproj"
COPY . .
WORKDIR "/src/VirtualStore.API"
RUN dotnet build "VirtualStore.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "VirtualStore.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "VirtualStore.API.dll"]
