# Build from the repository root:
#   docker build -f samples/MiPdf.WebApi/Dockerfile -t mipdflib-webapi .
#   docker run --rm -p 8080:8080 mipdflib-webapi
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/MiPdf/ src/MiPdf/
COPY samples/MiPdf.WebApi/ samples/MiPdf.WebApi/
RUN dotnet publish samples/MiPdf.WebApi/MiPdf.WebApi.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "MiPdf.WebApi.dll"]
