# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LabelVerifier.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Tesseract is the offline fallback engine (the path used behind TTB's firewall).
# Installing it in the image means the fallback works on the live demo too.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tesseract-ocr \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# Azure App Service for Containers expects the app on 8080.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "LabelVerifier.dll"]
