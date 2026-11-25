# Servidor de ARCANA (servicio único)

Un solo servicio .NET 8 para el juego: cuentas con contraseña, competitivo, salas casuales por SSE, torneos en línea y
rotación semanal de cajas. Sustituye al antiguo servidor en Rust (sigue en el historial de git).

Esta carpeta es una **copia generada**: la fuente es `arcana-godot`. Para actualizarla, desde `arcana-godot/server`:
`./publicar-servidor.ps1`, y luego `git add . ; git commit ; git push origin main`.

## Render

- Runtime **Docker**, `Dockerfile` en la raíz. Render define `PORT` y el servicio escucha ahí (25565 si no existe).
- Comprobación de salud: `/health`.
- **Disco persistente** montado en `/var/data` (o apunta `ARCANA_RANKED_DATA` a tu disco). Ahí viven `ladder.json` (cuentas y
  clasificación) y `seal.secret`. Sin disco, las cuentas se borran en cada despliegue.
- Una sola instancia: la cola y las salas viven en memoria.

## Local

```
dotnet run --project server/arcana-server/competitive
```

Escucha en `http://127.0.0.1:25565`. `--selftest` ejecuta la autocomprobación de la rotación de cajas.
