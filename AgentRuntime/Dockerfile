# AgentCore Runtime requires linux/arm64 images listening on port 8080.
# Build context is this folder: docker buildx build --platform linux/arm64 -t agentcore-maf:latest AgentRuntime
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY AgentRuntime.csproj .
RUN dotnet restore AgentRuntime.csproj -a $TARGETARCH
COPY . .
RUN dotnet publish AgentRuntime.csproj -c Release -a $TARGETARCH --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AgentRuntime.dll"]
