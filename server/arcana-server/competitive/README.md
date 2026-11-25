# ARCANA · Servidor (único servicio)

Servicio .NET 8 único de ARCANA: cuentas con contraseña, competitivo, salas casuales por SSE, torneos en línea y rotación semanal de cajas (lo que hacía `arcana-server-render`, ya retirado). Usa el mismo motor de reglas que el cliente. Empareja a dos jugadores y aplica las acciones y decisiones del duelo dentro del servidor antes de calcular los PR.

## Inicio local

Desde la raíz del proyecto ejecuta `dotnet run --project server/arcana-server/competitive`. Abre **Competitivo → Conexión** en los dos clientes y usa `http://127.0.0.1:25565` (en «Servidor en línea» basta una sola URL para todo). Usa dos perfiles distintos y elige un mazo con 40 cartas principales y un Arcano. Para jugar desde otra máquina configura una URL HTTPS.

## Render

El ZIP independiente de servidor incluye `render.yaml` en la raíz, el Dockerfile, el catálogo de cartas y las reglas compartidas. Súbelo a un repositorio propio y conéctalo como Blueprint. Al arrancar, `/health` devuelve el estado del servicio. Configura su URL HTTPS en ARCANA. Las salas casuales y la tienda semanal siguen usando la URL de su servidor actual.

El servicio guarda la clasificación, temporadas pasadas y resultados en `ladder.json` dentro de `ARCANA_RANKED_DATA`. Monta un disco persistente y usa una sola instancia, ya que la cola y las salas viven en memoria. Ajusta la próxima temporada en `ranked.config.json` antes de comenzar a puntuar.

La validación de la estructura del mazo y del resultado del duelo ocurre en el servidor. La propiedad de las cartas se comprueba en el cliente, porque la colección de ARCANA se guarda localmente. El registro competitivo utiliza una clave de perfil local. Para mantener el mismo rango en otro equipo importa una copia privada del perfil; exportar un código de mazo jamás incluye esa clave.

## Ejemplos y pruebas

Desde el proyecto Godot, `dotnet run --project tools/Arcana.Tools -- mazos` ejecuta las pruebas del parser y de rangos. `docs/arcana_visual/ejemplos/Perfil_Liria.json` se puede importar desde la pantalla inicial para ver el perfil de ejemplo. Incluye el mazo «Azar de medianoche» de 38/40, una vitrina de tres cartas y resultados de muestra. El asistente ofrece predeterminados cuyas cartas todavía le faltan a Liria. `Importar_mazo.txt` contiene un ID inexistente, cantidades excesivas y un ID repetido.
