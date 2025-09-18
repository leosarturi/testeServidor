# Etapa 1 - Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copia csproj e restaura dependências
COPY /ServidorLocal/*.csproj ./
RUN dotnet restore

# Copia todo o código e compila
COPY ServidorLocal/. ./ServidorLocal
RUN dotnet publish ./ServidorLocal/ServidorLocal.csproj -c Release -o /app

# Etapa 2 - Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .

# Expõe a porta (ajuste se não for 5260)
EXPOSE 5260

# Comando de entrada
ENTRYPOINT ["dotnet", "ServidorLocal.dll"]
