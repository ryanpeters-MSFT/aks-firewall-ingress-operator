FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["FirewallSync.csproj", "./"]
RUN dotnet restore "FirewallSync.csproj"
COPY . .
RUN dotnet publish "FirewallSync.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "FirewallSync.dll"]
