//! Servidor de ARCANA (port de server.ts): perfiles en un único JSON, salas por código con SSE
//! (cada jugador escucha con GET y publica con POST; el servidor solo reenvía), espectadores y los
//! archivos del juego (dist/). Electron lo arranca con `--port 0 --watch-stdin` y lee la línea
//! «ARCANA_LISTO http://…» para saber dónde cargar.
//! shortcut: sin autenticación (quien sabe el código o el nombre puede usarlos) y salas en memoria,
//! igual que server.ts; añadir sesiones si el juego sale de la red local.

use std::{
    collections::HashMap,
    convert::Infallible,
    hash::{BuildHasher, Hasher},
    io::{Read, Write},
    path::PathBuf,
    pin::Pin,
    sync::{
        atomic::{AtomicU64, Ordering},
        Arc, Mutex, MutexGuard,
    },
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use axum::{
    body::Bytes,
    extract::{DefaultBodyLimit, Path, State},
    http::StatusCode,
    response::{
        sse::{Event, KeepAlive, Sse},
        IntoResponse, Response,
    },
    routing::{any, get},
    Json, Router,
};
use serde_json::{json, Map, Value};
use tokio::sync::mpsc::{unbounded_channel, UnboundedSender};
use tokio_stream::{wrappers::UnboundedReceiverStream, Stream, StreamExt};
use tower_http::services::{ServeDir, ServeFile};

const MAX_BODY: usize = 64 * 1024;
const MAX_LOG: usize = 5000; // una partida larga ronda unos cientos de mensajes

type Tx = UnboundedSender<Value>;
type Flujo = Pin<Box<dyn Stream<Item = Result<Event, Infallible>> + Send>>;

#[derive(Default)]
struct Info {
    h: Option<String>,
    g: Option<String>,
    dh: Option<String>,
    dg: Option<String>,
    desde: Option<u64>,
}

/// Una sala: los dos jugadores (id de conexión + canal), la semilla de la partida, su registro
/// (para los espectadores que llegan tarde) y los espectadores.
#[derive(Default)]
struct Sala {
    h: Option<(u64, Tx)>,
    g: Option<(u64, Tx)>,
    seed: Option<u32>,
    log: Vec<Value>,
    ver: Vec<(u64, Tx)>,
    info: Option<Info>,
}

/// Canal de un torneo en línea: hasta MAX_CANAL miembros; lo que publica uno llega a los demás.
/// Guarda el último «estado» (lo publica el anfitrión) para quien entra tarde.
#[derive(Default)]
struct Canal {
    miembros: Vec<(String, u64, Tx)>,
    estado: Option<Value>,
}
const MAX_CANAL: usize = 9;

struct App {
    perfiles: PathBuf,
    salas: Mutex<HashMap<String, Sala>>,
    canales: Mutex<HashMap<String, Canal>>,
    ids: AtomicU64,
    disco: Mutex<()>, // las escrituras del archivo de perfiles van de una en una
}
type St = Arc<App>;

#[derive(Clone, Copy)]
enum Rol {
    H,
    G,
    E,
}

fn bloquea<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|p| p.into_inner()) // un pánico en otra petición no tumba el servidor
}

impl App {
    /// Lo que server.ts hace en `req.on("close")`: el jugador o espectador `id` se fue.
    fn salir(&self, code: &str, rol: Rol, id: u64) {
        let mut salas = bloquea(&self.salas);
        let Some(sala) = salas.get_mut(code) else { return };
        let host = match rol {
            Rol::E => {
                sala.ver.retain(|(i, _)| *i != id);
                return;
            }
            Rol::H => true,
            Rol::G => false,
        };
        let mio = if host { &mut sala.h } else { &mut sala.g };
        if mio.as_ref().map(|(i, _)| *i) != Some(id) {
            return; // ya lo ocupa otra conexión
        }
        *mio = None;
        sala.seed = None; // la siguiente partida de la sala lanza otra moneda
        let otro = if host { &sala.g } else { &sala.h };
        if let Some((_, tx)) = otro {
            let _ = tx.send(json!({ "t": "salio" }));
        }
        let de = if host { "h" } else { "g" };
        for (_, v) in sala.ver.drain(..) {
            let _ = v.send(json!({ "t": "salio", "de": de })); // al soltar el canal su flujo termina
        }
        sala.info = None;
        if host || sala.h.is_none() {
            salas.remove(code); // sin anfitrión la sala se cierra (y el invitado queda desconectado)
        }
    }
}

impl App {
    /// Un miembro del canal se fue: si era el anfitrión el canal se cierra; si no, se avisa a los demás.
    fn salir_canal(&self, code: &str, quien: &str, id: u64) {
        let mut canales = bloquea(&self.canales);
        let Some(canal) = canales.get_mut(code) else { return };
        if !canal.miembros.iter().any(|(q, i, _)| q == quien && *i == id) {
            return;
        }
        canal.miembros.retain(|(_, i, _)| *i != id);
        if quien == "h" {
            for (_, _, tx) in canal.miembros.drain(..) {
                let _ = tx.send(json!({ "t": "cerrado" }));
            }
            canales.remove(code);
        } else {
            for (_, _, tx) in &canal.miembros {
                let _ = tx.send(json!({ "t": "sale", "de": quien }));
            }
        }
    }
}
struct SalidaCanal {
    app: St,
    code: String,
    quien: String,
    id: u64,
}
impl Drop for SalidaCanal {
    fn drop(&mut self) {
        self.app.salir_canal(&self.code, &self.quien, self.id);
    }
}
fn quien_valido(q: &str) -> bool {
    !q.is_empty() && q.len() <= 12 && q.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit())
}

/// GET: entra al canal (`h` lo crea). Recibe {t:"ok"}, el último estado y después lo que publiquen los demás.
async fn escuchar_canal(State(app): State<St>, Path((code, quien)): Path<(String, String)>) -> Response {
    if !codigo_valido(&code) || !quien_valido(&quien) {
        return no_existe();
    }
    let mut canales = bloquea(&app.canales);
    let error = match (quien == "h", canales.get(&code)) {
        (true, Some(_)) => "Ese código ya está en uso",
        (true, None) => "",
        (false, None) => "El torneo no existe",
        (false, Some(c)) if c.miembros.len() >= MAX_CANAL => "El torneo está lleno",
        (false, Some(c)) if c.miembros.iter().any(|(q, _, _)| *q == quien) => "Ya estás en ese torneo",
        (false, Some(_)) => "",
    };
    if !error.is_empty() {
        return solo_error(error).into_response();
    }
    let id = app.ids.fetch_add(1, Ordering::Relaxed);
    let (tx, rx) = unbounded_channel();
    let _ = tx.send(json!({ "t": "ok" }));
    let canal = canales.entry(code.clone()).or_default();
    if let Some(e) = &canal.estado {
        let _ = tx.send(e.clone());
    }
    canal.miembros.push((quien.clone(), id, tx));
    drop(canales);
    let salida = SalidaCanal { app: app.clone(), code, quien, id };
    sse(Box::pin(UnboundedReceiverStream::new(rx).map(move |v| {
        let _vivo = &salida;
        ev(&v)
    })))
    .into_response()
}

/// POST: reenvía el mensaje (con `de`) al resto del canal; un {t:"estado"} queda guardado.
async fn publicar_canal(State(app): State<St>, Path((code, quien)): Path<(String, String)>, body: Bytes) -> Response {
    if !codigo_valido(&code) || !quien_valido(&quien) {
        return no_existe();
    }
    let Ok(Value::Object(mut obj)) = serde_json::from_slice::<Value>(&body) else {
        return (StatusCode::BAD_REQUEST, Json(json!("cuerpo inválido"))).into_response();
    };
    let mut canales = bloquea(&app.canales);
    let Some(canal) = canales.get_mut(&code).filter(|c| c.miembros.iter().any(|(q, _, _)| *q == quien)) else {
        return (StatusCode::NOT_FOUND, Json(json!(false))).into_response();
    };
    obj.insert("de".into(), json!(quien));
    let m = Value::Object(obj);
    if m.get("t").and_then(Value::as_str) == Some("estado") {
        canal.estado = Some(m.clone());
    }
    for (q, _, tx) in &canal.miembros {
        if *q != quien {
            let _ = tx.send(m.clone());
        }
    }
    Json(json!(true)).into_response()
}

/// Mientras vive el flujo SSE la conexión sigue abierta; al soltarlo (el cliente se fue) se avisa a la sala.
struct Salida {
    app: St,
    code: String,
    rol: Rol,
    id: u64,
}
impl Drop for Salida {
    fn drop(&mut self) {
        self.app.salir(&self.code, self.rol, self.id);
    }
}

fn ev(v: &Value) -> Result<Event, Infallible> {
    Ok(Event::default().data(v.to_string()))
}
fn sse(s: Flujo) -> Sse<Flujo> {
    Sse::new(s).keep_alive(KeepAlive::new().interval(Duration::from_secs(15)))
}
fn solo_error(msg: &str) -> Sse<Flujo> {
    let v = json!({ "t": "error", "msg": msg });
    sse(Box::pin(tokio_stream::iter(vec![ev(&v)])))
}
fn flujo(app: &St, code: String, rol: Rol, id: u64, rx: tokio::sync::mpsc::UnboundedReceiver<Value>) -> Sse<Flujo> {
    let salida = Salida { app: app.clone(), code, rol, id };
    sse(Box::pin(UnboundedReceiverStream::new(rx).map(move |v| {
        let _vivo = &salida; // el cierre retiene la guarda: se suelta con el flujo
        ev(&v)
    })))
}

fn no_existe() -> Response {
    (StatusCode::NOT_FOUND, Json(Value::Null)).into_response()
}
fn nombre_valido(n: &str) -> bool {
    !n.is_empty() && n.chars().count() <= 20
}
fn codigo_valido(c: &str) -> bool {
    c.len() == 5 && c.bytes().all(|b| b.is_ascii_uppercase() || b.is_ascii_digit())
}
fn ahora_ms() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_millis() as u64).unwrap_or(0)
}
/// Semilla de la partida (y con ella la moneda A/B): la decide el servidor, no los jugadores.
fn semilla() -> u32 {
    let mut h = std::collections::hash_map::RandomState::new().build_hasher();
    h.write_u128(SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0));
    (h.finish() & 0x7fff_ffff) as u32
}

fn leer_perfiles(path: &PathBuf) -> Map<String, Value> {
    std::fs::read_to_string(path)
        .ok()
        .and_then(|s| serde_json::from_str::<Value>(&s).ok())
        .and_then(|v| match v {
            Value::Object(m) => Some(m),
            _ => None,
        })
        .unwrap_or_default()
}

async fn get_perfil(State(app): State<St>, Path(name): Path<String>) -> Response {
    if !nombre_valido(&name) {
        return no_existe();
    }
    match leer_perfiles(&app.perfiles).remove(&name) {
        Some(v) => Json(v).into_response(),
        None => no_existe(),
    }
}

async fn put_perfil(State(app): State<St>, Path(name): Path<String>, body: Bytes) -> Response {
    if !nombre_valido(&name) {
        return no_existe();
    }
    let Ok(v) = serde_json::from_slice::<Value>(&body) else {
        return (StatusCode::BAD_REQUEST, Json(json!("cuerpo inválido"))).into_response();
    };
    let _turno = bloquea(&app.disco);
    let mut todos = leer_perfiles(&app.perfiles);
    todos.insert(name, v);
    if let Some(dir) = app.perfiles.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    // temporal + renombrar: un corte a mitad de escritura no deja el archivo de perfiles roto
    let tmp = app.perfiles.with_extension("json.tmp");
    let texto = serde_json::to_string_pretty(&Value::Object(todos)).unwrap_or_default();
    match std::fs::write(&tmp, texto).and_then(|_| std::fs::rename(&tmp, &app.perfiles)) {
        Ok(()) => Json(json!(true)).into_response(),
        Err(e) => (StatusCode::INTERNAL_SERVER_ERROR, Json(json!(e.to_string()))).into_response(),
    }
}

async fn salas(State(app): State<St>) -> Json<Value> {
    let salas = bloquea(&app.salas);
    let vivas: Vec<Value> = salas
        .iter()
        .filter_map(|(code, s)| {
            let info = s.info.as_ref()?;
            let desde = info.desde?;
            if s.h.is_none() || s.g.is_none() {
                return None;
            }
            Some(json!({
                "code": code,
                "h": info.h.clone().unwrap_or_else(|| "?".into()),
                "g": info.g.clone().unwrap_or_else(|| "?".into()),
                "dh": info.dh.clone().unwrap_or_default(),
                "dg": info.dg.clone().unwrap_or_default(),
                "desde": desde,
                "ver": s.ver.len(),
            }))
        })
        .collect();
    Json(Value::Array(vivas))
}

async fn escuchar(State(app): State<St>, Path((code, rol)): Path<(String, String)>) -> Response {
    if !codigo_valido(&code) {
        return no_existe();
    }
    match rol.as_str() {
        "e" => espectar(&app, code).into_response(),
        "h" => jugar(&app, code, true).into_response(),
        "g" => jugar(&app, code, false).into_response(),
        _ => no_existe(),
    }
}

fn jugar(app: &St, code: String, host: bool) -> Sse<Flujo> {
    let mut salas = bloquea(&app.salas);
    let error = match (host, salas.get(&code)) {
        (true, Some(_)) => "Ese código ya está en uso",
        (true, None) => "",
        (false, None) => "La sala no existe",
        (false, Some(s)) if s.h.is_none() => "La sala no existe",
        (false, Some(s)) if s.g.is_some() => "La sala está llena",
        (false, Some(_)) => "",
    };
    if !error.is_empty() {
        return solo_error(error);
    }
    let id = app.ids.fetch_add(1, Ordering::Relaxed);
    let (tx, rx) = unbounded_channel();
    let _ = tx.send(json!({ "t": "ok" }));
    let sala = salas.entry(code.clone()).or_default();
    if host {
        sala.h = Some((id, tx));
    } else {
        sala.g = Some((id, tx));
    }
    drop(salas);
    flujo(app, code, if host { Rol::H } else { Rol::G }, id, rx)
}

/// Espectador: recibe el registro entero de la partida y después cada mensaje en directo. No puede enviar.
fn espectar(app: &St, code: String) -> Sse<Flujo> {
    let mut salas = bloquea(&app.salas);
    let Some(sala) = salas.get_mut(&code).filter(|s| s.h.is_some() && s.g.is_some()) else {
        return solo_error("Esa partida no existe o aún no ha empezado");
    };
    let (tx, rx) = unbounded_channel();
    let _ = tx.send(json!({ "t": "ok" }));
    for m in &sala.log {
        let _ = tx.send(m.clone());
    }
    let id = app.ids.fetch_add(1, Ordering::Relaxed);
    sala.ver.push((id, tx));
    drop(salas);
    flujo(app, code, Rol::E, id, rx)
}

async fn publicar(State(app): State<St>, Path((code, rol)): Path<(String, String)>, body: Bytes) -> Response {
    let host = match rol.as_str() {
        "h" => true,
        "g" => false,
        _ => return no_existe(),
    };
    if !codigo_valido(&code) {
        return no_existe();
    }
    let Ok(mut m) = serde_json::from_slice::<Value>(&body) else {
        return (StatusCode::BAD_REQUEST, Json(json!("cuerpo inválido"))).into_response();
    };
    let mut salas = bloquea(&app.salas);
    let Some(sala) = salas.get_mut(&code) else {
        return (StatusCode::NOT_FOUND, Json(json!(false))).into_response();
    };
    let otro = if host { &sala.g } else { &sala.h };
    if otro.is_none() {
        return (StatusCode::NOT_FOUND, Json(json!(false))).into_response();
    }
    let t = m.get("t").and_then(Value::as_str).map(str::to_owned);
    let empieza = matches!(t.as_deref(), Some("hola") | Some("inicio"));
    if let Value::Object(obj) = &mut m {
        if empieza {
            let seed = *sala.seed.get_or_insert_with(semilla);
            obj.insert("seed".into(), json!(seed));
        }
    }
    if let Some((_, tx)) = if host { &sala.g } else { &sala.h } {
        let _ = tx.send(m.clone());
    }
    if let Value::Object(obj) = &m {
        if t.as_deref() == Some("hola") {
            sala.log.clear(); // empieza una partida nueva en la sala
            sala.info = Some(Info::default());
        }
        if empieza {
            let info = sala.info.get_or_insert_with(Info::default);
            let nombre = obj.get("name").and_then(Value::as_str).map(|s| s.chars().take(20).collect()).unwrap_or_else(|| "?".to_string());
            let mazo = m.pointer("/deck/id").and_then(Value::as_str).unwrap_or("").to_string();
            if host {
                info.h = Some(nombre);
                info.dh = Some(mazo);
            } else {
                info.g = Some(nombre);
                info.dg = Some(mazo);
            }
            if t.as_deref() == Some("inicio") {
                info.desde = Some(ahora_ms());
            }
        }
        let mut con_de = obj.clone();
        con_de.insert("de".into(), json!(if host { "h" } else { "g" }));
        let con_de = Value::Object(con_de);
        if sala.log.len() < MAX_LOG {
            sala.log.push(con_de.clone());
        }
        for (_, v) in &sala.ver {
            let _ = v.send(con_de.clone());
        }
    }
    Json(json!(true)).into_response()
}

struct Config {
    host: String,
    port: u16,
    estaticos: PathBuf,
    datos: PathBuf,
    vigila_stdin: bool,
}

fn config() -> Config {
    let mut c = Config {
        host: "127.0.0.1".into(),
        port: 25565,
        estaticos: PathBuf::from("dist"),
        datos: PathBuf::from("data/perfiles.json"),
        vigila_stdin: false,
    };
    let mut args = std::env::args().skip(1);
    while let Some(a) = args.next() {
        match a.as_str() {
            "--host" => c.host = args.next().unwrap_or(c.host),
            "--lan" => c.host = "0.0.0.0".into(), // abre el servidor a la red local (partidas en línea entre equipos)
            "--port" => c.port = args.next().and_then(|p| p.parse().ok()).unwrap_or(c.port),
            "--static" => c.estaticos = args.next().map(PathBuf::from).unwrap_or(c.estaticos),
            "--data" => c.datos = args.next().map(PathBuf::from).unwrap_or(c.datos),
            "--watch-stdin" => c.vigila_stdin = true,
            otro => eprintln!("argumento desconocido: {otro}"),
        }
    }
    c
}

#[tokio::main]
async fn main() {
    let c = config();
    let app: St = Arc::new(App {
        perfiles: c.datos.clone(),
        salas: Mutex::new(HashMap::new()),
        canales: Mutex::new(HashMap::new()),
        ids: AtomicU64::new(1),
        disco: Mutex::new(()),
    });
    let index = c.estaticos.join("index.html");
    let router = Router::new()
        .route("/api/perfil/:name", get(get_perfil).put(put_perfil))
        .route("/api/salas", get(salas))
        .route("/api/sala/:code/:rol", get(escuchar).post(publicar))
        .route("/api/canal/:code/:quien", get(escuchar_canal).post(publicar_canal))
        .route("/api/*resto", any(|| async { no_existe() }))
        .fallback_service(ServeDir::new(&c.estaticos).fallback(ServeFile::new(index)))
        .layer(DefaultBodyLimit::max(MAX_BODY))
        .with_state(app);

    let listener = match tokio::net::TcpListener::bind((c.host.as_str(), c.port)).await {
        Ok(l) => l,
        Err(e) => {
            eprintln!("No se pudo abrir {}:{}: {e}", c.host, c.port);
            std::process::exit(1);
        }
    };
    let addr = listener.local_addr().map(|a| a.to_string()).unwrap_or_default();
    println!("ARCANA_LISTO http://{addr}");
    let _ = std::io::stdout().flush();

    // Lanzado por Electron: si el proceso padre muere, su tubería de stdin se cierra y el servidor también.
    if c.vigila_stdin {
        std::thread::spawn(|| {
            let mut buf = [0u8; 256];
            let mut stdin = std::io::stdin();
            loop {
                match stdin.read(&mut buf) {
                    Ok(0) | Err(_) => std::process::exit(0),
                    Ok(_) => {}
                }
            }
        });
    }

    let fin = async {
        let _ = tokio::signal::ctrl_c().await;
    };
    if let Err(e) = axum::serve(listener, router).with_graceful_shutdown(fin).await {
        eprintln!("El servidor se detuvo: {e}");
    }
}
