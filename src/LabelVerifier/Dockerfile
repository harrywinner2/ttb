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

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Run as the image's built-in non-root user (least privilege). Tesseract uses /tmp, which is
# world-writable, and the app only reads from /app.
USER $APP_UID

ENTRYPOINT ["dotnet", "LabelVerifier.dll"]
