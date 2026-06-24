# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Somewhat.PdsR4Gateway.sln ./
COPY src/Somewhat.PdsR4Gateway/Somewhat.PdsR4Gateway.csproj src/Somewhat.PdsR4Gateway/
RUN dotnet restore src/Somewhat.PdsR4Gateway/Somewhat.PdsR4Gateway.csproj

COPY . .
RUN dotnet build src/Somewhat.PdsR4Gateway/Somewhat.PdsR4Gateway.csproj -c Release
RUN dotnet test
RUN dotnet publish src/Somewhat.PdsR4Gateway/Somewhat.PdsR4Gateway.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
	ASPNETCORE_ENVIRONMENT=Production \
	DOTNET_EnableDiagnostics=0 \
	DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

RUN useradd --create-home --uid 64198 --shell /usr/sbin/nologin appuser

COPY --from=build --chown=appuser:appuser /app/publish .

USER appuser

ENTRYPOINT ["dotnet", "Somewhat.PdsR4Gateway.dll"]
