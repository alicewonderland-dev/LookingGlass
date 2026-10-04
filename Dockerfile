# Server image. Build:  docker build -t lookingglass .
# Run:                  docker run -p 127.0.0.1:5180:5180 -v lookingglass-data:/app/data lookingglass
# Put a TLS reverse proxy in front for anything beyond local testing.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/LookingGlass.Protocol/ src/LookingGlass.Protocol/
COPY src/LookingGlass.Core/ src/LookingGlass.Core/
COPY src/LookingGlass.Server/ src/LookingGlass.Server/
RUN dotnet publish src/LookingGlass.Server -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# The data folder must exist and belong to the app user before the volume is mounted,
# or SQLite can't create the database.
RUN mkdir -p /app/data && chown $APP_UID /app/data
VOLUME /app/data
EXPOSE 5180
USER $APP_UID
# Inside the container, listen on all interfaces; the port mapping above decides exposure.
ENTRYPOINT ["dotnet", "LookingGlass.Server.dll", "--urls", "http://0.0.0.0:5180"]
