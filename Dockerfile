# Servicio único de ARCANA (cuentas, competitivo, salas, torneos y rotación de cajas). Contexto: la raíz del repo.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /arcana
COPY src/ ./src/
COPY content/ ./content/
COPY server/arcana-server/competitive/ ./server/arcana-server/competitive/
RUN dotnet publish server/arcana-server/competitive/Arcana.RankedServer.csproj -c Release -o /out -p:ArcanaContentRoot=/arcana/content

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out/ ./
# cuentas, clasificación y secreto de sellos: monta aquí un disco persistente o se pierden en cada despliegue
ENV ARCANA_RANKED_DATA=/var/data
EXPOSE 25565
ENTRYPOINT ["dotnet", "Arcana.RankedServer.dll"]
