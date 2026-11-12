# Build the API (framework-only: no NuGet packages needed)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/PolicyPilot.Api -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
COPY data ./data
ENV ASPNETCORE_URLS=http://+:8080 Data__DocsFolder=/app/data/docs
EXPOSE 8080
ENTRYPOINT ["dotnet", "PolicyPilot.Api.dll"]
