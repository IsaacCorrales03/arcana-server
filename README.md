# Servidor de ARCANA

Perfiles y salas en línea (SSE) para los clientes Godot y web. Solo la API, sin versión web. Puerto **25565**.

## Con Docker

```bash
docker compose up -d --build
```

Los perfiles se guardan en el volumen `arcana-datos` (archivo `/data/perfiles.json`).
Ver el registro: `docker logs -f arcana` (al arrancar escribe `ARCANA_LISTO http://0.0.0.0:25565`).
Parar: `docker compose down` (los perfiles se conservan en el volumen).

## Sin Docker (con Cargo)

```bash
cargo build --release --locked
./target/release/arcana-server --lan --data data/perfiles.json
```

`--lan` escucha en todas las interfaces (0.0.0.0); sin él, solo en este equipo (127.0.0.1).
Otras opciones: `--port N`, `--host IP`, `--static carpeta` (sirve una versión web compilada).

## Conectar el juego

En ARCANA: Configuración → «Servidor en línea» → `http://IP-del-servidor:25565`.
Si el servidor está fuera de tu red, abre el puerto 25565 (TCP) en su cortafuegos o router.
Un jugador crea la sala desde «En línea» y el otro se une con el código.

## Comprobar que responde

```bash
curl http://localhost:25565/api/salas
```

Devuelve la lista de salas abiertas (`[]` si no hay ninguna).
