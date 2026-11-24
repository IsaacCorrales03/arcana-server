# Servidor de ARCANA (solo la API: perfiles y salas en línea por SSE). Sin versión web.
#   docker build -t arcana-server .
#   docker run -d --name arcana -p 25565:25565 -v arcana-datos:/data arcana-server

# 1) compilar (las versiones fijadas en Cargo.lock compilan igual en Linux)
FROM rust:1-bookworm AS build
WORKDIR /src
COPY Cargo.toml Cargo.lock ./
COPY src ./src
RUN cargo build --release --locked

# 2) imagen final: solo el binario y un usuario sin privilegios
FROM debian:bookworm-slim
RUN useradd --system --uid 10001 --create-home arcana && mkdir -p /data && chown arcana /data
COPY --from=build /src/target/release/arcana-server /app/arcana-server
USER arcana
WORKDIR /app
# los perfiles viven en /data: monta ahí un volumen para no perderlos al recrear el contenedor
VOLUME ["/data"]
EXPOSE 25565
# el servidor se cierra limpio con Ctrl+C (SIGINT); Docker manda SIGTERM por defecto
STOPSIGNAL SIGINT
ENTRYPOINT ["/app/arcana-server", "--host", "0.0.0.0", "--port", "25565", "--data", "/data/perfiles.json"]
