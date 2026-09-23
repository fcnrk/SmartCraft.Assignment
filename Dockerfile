# POC container image (docs/06-implementation-plan.md scope). Runs with
# ASPNETCORE_ENVIRONMENT=Development so Swagger and POST /dev/token (the POC's stand-in for an
# identity provider) are available — see README.md "Known limitations" for why this is not a
# production posture.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY SmartCraft.Assignment.slnx ./
COPY src/SmartCraft.Assignment.Api/SmartCraft.Assignment.Api.csproj src/SmartCraft.Assignment.Api/
COPY tests/SmartCraft.Assignment.Tests/SmartCraft.Assignment.Tests.csproj tests/SmartCraft.Assignment.Tests/
RUN dotnet restore src/SmartCraft.Assignment.Api/SmartCraft.Assignment.Api.csproj

COPY src/SmartCraft.Assignment.Api/ src/SmartCraft.Assignment.Api/
RUN dotnet publish src/SmartCraft.Assignment.Api/SmartCraft.Assignment.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Development
ENV ASPNETCORE_URLS=http://+:8080
# SQLite file on a separate volume-friendly path, not inside the image's writable layer.
ENV ConnectionStrings__Default="Data Source=/data/smartcraft.db;Foreign Keys=True"
RUN mkdir -p /data
EXPOSE 8080

ENTRYPOINT ["dotnet", "SmartCraft.Assignment.Api.dll"]
