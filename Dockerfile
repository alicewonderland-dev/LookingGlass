# Server image. Build:  docker build -t wonderlandchat .
# Run:                  docker run -p 5180:5180 -v wonderlandchat-data:/app/data wonderlandchat
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/WonderlandChat.Protocol/ src/WonderlandChat.Protocol/
COPY src/WonderlandChat.Core/ src/WonderlandChat.Core/
COPY src/WonderlandChat.Server/ src/WonderlandChat.Server/
RUN dotnet publish src/WonderlandChat.Server -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
VOLUME /app/data
EXPOSE 5180
USER $APP_UID
ENTRYPOINT ["dotnet", "WonderlandChat.Server.dll", "--urls", "http://0.0.0.0:5180"]
