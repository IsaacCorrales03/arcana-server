using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;

// Salas casuales, canales de torneo y rotación semanal de cajas (antes en arcana-server-render, en Rust):
// el servidor solo reenvía mensajes por SSE. Cada jugador escucha con GET y publica con POST.
// shortcut: sin autenticación (quien sabe el código entra) y todo en memoria; si hace falta, atar las salas a las cuentas.
sealed class Rooms
{
    const int MaxLog = 5000, MaxTournament = 9; // una partida larga ronda unos cientos de mensajes
    sealed record Peer(string Who, long Id, Channel<string> Out);
    sealed class Info { public string? H, G, Dh, Dg; public long? Since; }
    sealed class Room
    {
        public Peer? H, G;
        public int? Seed;
        public readonly List<string> Log = [];
        public readonly List<Peer> Viewers = [];
        public Info? Info;
    }
    sealed class Tournament
    {
        public readonly List<Peer> Members = [];
        public string? State;
    }

    readonly object gate = new();
    readonly Dictionary<string, Room> rooms = [];
    readonly Dictionary<string, Tournament> tournaments = [];
    long ids;

    static bool CodeOk(string c) => c.Length == 5 && c.All(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9');
    static bool WhoOk(string q) => q.Length is >= 1 and <= 12 && q.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9');
    static string Tag(string t) => new JsonObject { ["t"] = t }.ToJsonString();
    static string Tag(string t, string key, string value) => new JsonObject { ["t"] = t, [key] = value }.ToJsonString();
    static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static IResult NotFound() => Results.Json<object?>(null, statusCode: 404);
    static IResult Missing() => Results.Json(false, statusCode: 404);
    static IResult BadBody() => Results.Json("cuerpo inválido", statusCode: 400);

    static async Task<JsonNode?> ReadBody(HttpContext http)
    {
        try { return await JsonNode.ParseAsync(http.Request.Body); }
        catch (System.Text.Json.JsonException) { return BadMarker; }
    }
    static readonly JsonNode BadMarker = JsonValue.Create("bad")!;

    /// <summary>Mantiene abierta la conexión SSE hasta que el cliente se va o el canal se cierra; al final avisa con onClose.</summary>
    static async Task Stream(HttpContext http, Channel<string> channel, Action onClose)
    {
        var ct = http.RequestAborted;
        try
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            await http.Response.StartAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tick.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await channel.Reader.WaitToReadAsync(tick.Token)) break;
                    while (channel.Reader.TryRead(out var message)) await http.Response.WriteAsync("data: " + message + "\n\n", ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await http.Response.WriteAsync(": ping\n\n", ct); }
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { onClose(); }
    }

    static Task Failure(HttpContext http, string message)
    {
        var channel = Channel.CreateUnbounded<string>();
        channel.Writer.TryWrite(Tag("error", "msg", message));
        channel.Writer.TryComplete();
        return Stream(http, channel, () => { });
    }

    // ---------- Salas de duelo ----------

    /// <summary>Roles: h anfitrión, g invitado, e espectador.</summary>
    public Task Listen(HttpContext http, string code, string role)
    {
        if (!CodeOk(code) || role is not ("h" or "g" or "e")) return WriteNotFound(http);
        var channel = Channel.CreateUnbounded<string>();
        long id = Interlocked.Increment(ref ids);
        string? error = null;
        lock (gate)
        {
            rooms.TryGetValue(code, out var room);
            if (role == "e")
            {
                if (room is not { H: not null, G: not null }) error = "Esa partida no existe o aún no ha empezado";
                else
                {
                    channel.Writer.TryWrite(Tag("ok"));
                    foreach (var message in room.Log) channel.Writer.TryWrite(message);
                    room.Viewers.Add(new(role, id, channel));
                }
            }
            else
            {
                bool host = role == "h";
                error = (host, room) switch
                {
                    (true, not null) => "Ese código ya está en uso",
                    (false, null) or (false, { H: null }) => "La sala no existe",
                    (false, { G: not null }) => "La sala está llena",
                    _ => null,
                };
                if (error is null)
                {
                    channel.Writer.TryWrite(Tag("ok"));
                    room ??= rooms[code] = new Room();
                    if (host) room.H = new(role, id, channel); else room.G = new(role, id, channel);
                }
            }
        }
        return error is not null ? Failure(http, error) : Stream(http, channel, () => Leave(code, role, id));
    }

    static Task WriteNotFound(HttpContext http)
    {
        http.Response.StatusCode = 404;
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync("null");
    }

    /// <summary>El jugador o espectador id se fue: se avisa al resto y, sin anfitrión, la sala se cierra.</summary>
    void Leave(string code, string role, long id)
    {
        lock (gate)
        {
            if (!rooms.TryGetValue(code, out var room)) return;
            if (role == "e") { room.Viewers.RemoveAll(v => v.Id == id); return; }
            bool host = role == "h";
            if ((host ? room.H : room.G)?.Id != id) return; // ya lo ocupa otra conexión
            if (host) room.H = null; else room.G = null;
            room.Seed = null; // la siguiente partida de la sala lanza otra moneda
            (host ? room.G : room.H)?.Out.Writer.TryWrite(Tag("salio"));
            foreach (var viewer in room.Viewers)
            {
                viewer.Out.Writer.TryWrite(Tag("salio", "de", host ? "h" : "g"));
                viewer.Out.Writer.TryComplete();
            }
            room.Viewers.Clear();
            room.Info = null;
            if (host || room.H is null)
            {
                room.G?.Out.Writer.TryComplete(); // sin anfitrión el invitado queda desconectado
                rooms.Remove(code);
            }
        }
    }

    public async Task<IResult> Publish(HttpContext http, string code, string role)
    {
        if (role is not ("h" or "g") || !CodeOk(code)) return NotFound();
        var body = await ReadBody(http);
        if (ReferenceEquals(body, BadMarker)) return BadBody();
        bool host = role == "h";
        lock (gate)
        {
            if (!rooms.TryGetValue(code, out var room) || (host ? room.G : room.H) is not { } other) return Missing();
            var obj = body as JsonObject;
            var type = Text(obj?["t"]);
            bool starts = type is "hola" or "inicio";
            if (obj is not null && starts) obj["seed"] = room.Seed ??= RandomNumberGenerator.GetInt32(1, int.MaxValue); // la moneda la decide el servidor
            other.Out.Writer.TryWrite(body?.ToJsonString() ?? "null");
            if (obj is null) return Results.Json(true);
            if (type == "hola") { room.Log.Clear(); room.Info = new(); } // empieza una partida nueva en la sala
            if (starts)
            {
                var info = room.Info ??= new();
                var name = Text(obj["name"]) is { } n ? new string(n.Take(20).ToArray()) : "?";
                var deck = obj["deck"] is JsonObject d ? Text(d["id"]) ?? "" : "";
                if (host) { info.H = name; info.Dh = deck; } else { info.G = name; info.Dg = deck; }
                if (type == "inicio") info.Since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            var tagged = obj.DeepClone().AsObject();
            tagged["de"] = host ? "h" : "g";
            var line = tagged.ToJsonString();
            if (room.Log.Count < MaxLog) room.Log.Add(line);
            foreach (var viewer in room.Viewers) viewer.Out.Writer.TryWrite(line);
            return Results.Json(true);
        }
    }

    /// <summary>Partidas en curso (con las dos plazas ocupadas) para los espectadores.</summary>
    public IResult Live()
    {
        var list = new JsonArray();
        lock (gate)
            foreach (var (code, room) in rooms)
                if (room is { H: not null, G: not null, Info: { Since: { } since } info })
                    list.Add(new JsonObject
                    {
                        ["code"] = code, ["h"] = info.H ?? "?", ["g"] = info.G ?? "?", ["dh"] = info.Dh ?? "", ["dg"] = info.Dg ?? "",
                        ["desde"] = since, ["ver"] = room.Viewers.Count,
                    });
        return Results.Content(list.ToJsonString(), "application/json");
    }

    // ---------- Canales de torneo en línea ----------

    public Task ListenTournament(HttpContext http, string code, string who)
    {
        if (!CodeOk(code) || !WhoOk(who)) return WriteNotFound(http);
        var channel = Channel.CreateUnbounded<string>();
        long id = Interlocked.Increment(ref ids);
        string? error;
        lock (gate)
        {
            tournaments.TryGetValue(code, out var tournament);
            bool host = who == "h";
            error = (host, tournament) switch
            {
                (true, not null) => "Ese código ya está en uso",
                (false, null) => "El torneo no existe",
                (false, { Members.Count: >= MaxTournament }) => "El torneo está lleno",
                (false, not null) when tournament.Members.Any(m => m.Who == who) => "Ya estás en ese torneo",
                _ => null,
            };
            if (error is null)
            {
                channel.Writer.TryWrite(Tag("ok"));
                tournament ??= tournaments[code] = new Tournament();
                if (tournament.State is { } state) channel.Writer.TryWrite(state); // para quien entra tarde
                tournament.Members.Add(new(who, id, channel));
            }
        }
        return error is not null ? Failure(http, error) : Stream(http, channel, () => LeaveTournament(code, who, id));
    }

    /// <summary>Si se va el anfitrión el torneo se cierra; si no, se avisa a los demás.</summary>
    void LeaveTournament(string code, string who, long id)
    {
        lock (gate)
        {
            if (!tournaments.TryGetValue(code, out var tournament) || !tournament.Members.Any(m => m.Who == who && m.Id == id)) return;
            tournament.Members.RemoveAll(m => m.Id == id);
            if (who == "h")
            {
                foreach (var member in tournament.Members)
                {
                    member.Out.Writer.TryWrite(Tag("cerrado"));
                    member.Out.Writer.TryComplete();
                }
                tournaments.Remove(code);
            }
            else foreach (var member in tournament.Members) member.Out.Writer.TryWrite(Tag("sale", "de", who));
        }
    }

    public async Task<IResult> PublishTournament(HttpContext http, string code, string who)
    {
        if (!CodeOk(code) || !WhoOk(who)) return NotFound();
        var body = await ReadBody(http);
        if (body is not JsonObject obj) return BadBody();
        lock (gate)
        {
            if (!tournaments.TryGetValue(code, out var tournament) || !tournament.Members.Any(m => m.Who == who)) return Missing();
            obj["de"] = who;
            var line = obj.ToJsonString();
            if (Text(obj["t"]) == "estado") tournament.State = line;
            foreach (var member in tournament.Members.Where(m => m.Who != who)) member.Out.Writer.TryWrite(line);
            return Results.Json(true);
        }
    }

    // ---------- Rotación semanal de cajas ----------

    const long WeekMs = 7L * 24 * 60 * 60 * 1000;
    const long EpochMs = 1_767_592_800_000; // 2026-01-05 06:00 UTC, lunes en Costa Rica
    static readonly string[][] Plan = [["fool", "empress", "devil"], ["magician", "lovers", "death"], ["priestess", "emperor", "fortune"]];

    static JsonObject Week(long index)
    {
        long begins = EpochMs + index * WeekMs;
        return new()
        {
            ["startsAtMs"] = begins, ["endsAtMs"] = begins + WeekMs,
            ["activeBoxIds"] = new JsonArray(Plan[(int)(((index % Plan.Length) + Plan.Length) % Plan.Length)].Select(id => (JsonNode)id).ToArray()),
        };
    }

    public static JsonObject Rotation(long nowMs)
    {
        long elapsed = nowMs - EpochMs, index = elapsed / WeekMs - (elapsed % WeekMs < 0 ? 1 : 0); // división hacia abajo
        return new()
        {
            ["serverNowMs"] = nowMs, ["week"] = Week(index),
            ["calendar"] = new JsonArray(Enumerable.Range(-2, 5).Select(offset => (JsonNode)Week(index + offset)).ToArray()),
        };
    }

    public static IResult RotationNow(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Content(Rotation(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToJsonString(), "application/json");
    }

    /// <summary>Autocomprobación para dotnet run -- --selftest (no hay proyecto de pruebas en el servidor).</summary>
    public static int SelfTest()
    {
        int fails = 0;
        void Is(bool ok, string label) { Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}"); if (!ok) fails++; }
        var last = Rotation(EpochMs + WeekMs - 1);
        var next = Rotation(EpochMs + WeekMs);
        Is(last["week"]!["endsAtMs"]!.GetValue<long>() == next["week"]!["startsAtMs"]!.GetValue<long>(), "rotación: una semana termina donde empieza la siguiente");
        Is(last["week"]!["activeBoxIds"]!.ToJsonString() != next["week"]!["activeBoxIds"]!.ToJsonString(), "rotación: las cajas cambian en el corte exacto");
        Is(Rotation(EpochMs - 1)["week"]!["activeBoxIds"]!.ToJsonString() == "[\"priestess\",\"emperor\",\"fortune\"]", "rotación: antes de la época usa el último grupo");
        Is(next["calendar"]!.AsArray().Count == 5 && next["week"]!["activeBoxIds"]!.AsArray().Count == 3, "rotación: calendario de 5 semanas y 3 cajas por semana");
        return fails;
    }
}
