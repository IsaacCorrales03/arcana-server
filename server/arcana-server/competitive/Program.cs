using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Arcana.Content;
using Arcana.Core;
using Arcana.Meta;

if (args.Contains("--selftest")) return Rooms.SelfTest();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024);
var app = builder.Build();
var directory = Environment.GetEnvironmentVariable("ARCANA_RANKED_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(directory);
var configPath = Path.Combine(AppContext.BaseDirectory, "ranked.config.json");
var limits = File.Exists(configPath) ? JsonSerializer.Deserialize<RankRules>(File.ReadAllText(configPath), Json.Options) ?? Ranked.Rules : Ranked.Rules;
if (limits.PointsPerDivision is < 20 or > 10000 || limits.SeasonDays is < 1 or > 365 || limits.Placements < 0 ||
    limits.Win <= 0 || limits.StreakBonus < 0 || limits.BonusAt < 1 || limits.Loss < 0 || limits.Grace < 0)
    throw new InvalidOperationException("Configuración competitiva inválida.");
var ladder = new Ladder(directory, limits);
app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapGet("/deck", (HttpRequest req) => Results.Text(
    "Copia este código en ARCANA · Mazos · Importar.\n\n" + req.Query["d"].ToString(), "text/plain; charset=utf-8"));
// Salas casuales, torneos en línea y rotación de cajas: lo que hacía arcana-server-render (el cliente usa una sola URL).
var casual = new Rooms();
app.MapGet("/api/salas", () => casual.Live());
app.MapGet("/api/tienda/rotacion", (HttpContext http) => Rooms.RotationNow(http));
app.MapGet("/api/sala/{code}/{rol}", (HttpContext http, string code, string rol) => casual.Listen(http, code, rol));
app.MapPost("/api/sala/{code}/{rol}", (HttpContext http, string code, string rol) => casual.Publish(http, code, rol));
app.MapGet("/api/canal/{code}/{quien}", (HttpContext http, string code, string quien) => casual.ListenTournament(http, code, quien));
app.MapPost("/api/canal/{code}/{quien}", (HttpContext http, string code, string quien) => casual.PublishTournament(http, code, quien));
app.MapPost("/api/ranked/register",(HttpContext http, Registration request) => ladder.Register(Key(http), request));
app.MapGet("/api/ranked/state", (HttpContext http) => ladder.State(Key(http)));
app.MapPost("/api/account/signup", (Credentials form) => ladder.Signup(form));
app.MapPost("/api/account/login", (Credentials form) => ladder.Login(form));
app.MapPost("/api/account/password", (HttpContext http, Credentials form) => ladder.SetPassword(Key(http), form));
app.MapPost("/api/account/tamper", (HttpContext http) => ladder.Tamper(Key(http)));
app.MapPost("/api/collection/craft/{cardId}", (HttpContext http, string cardId) => ladder.Craft(Key(http), cardId));
app.MapPost("/api/ranked/queue", (HttpContext http, DeckDefinition deck) => ladder.Enqueue(Key(http), deck));
app.MapDelete("/api/ranked/queue", (HttpContext http) => ladder.Cancel(Key(http)));
app.MapGet("/api/ranked/receipt/{id}", (HttpContext http, string id) => ladder.Receipt(Key(http), id));
app.MapPost("/api/ranked/room/{id}", (HttpContext http, string id, JsonObject message) => ladder.Receive(Key(http), id, message));
app.MapGet("/api/ranked/room/{id}", async (HttpContext http, string id) =>
{
    var listener = ladder.Listen(Key(http), id);
    if (listener is null) { http.Response.StatusCode = 404; return; }
    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    await http.Response.StartAsync(http.RequestAborted);
    try
    {
        await foreach (var message in listener.Reader.ReadAllAsync(http.RequestAborted))
        {
            await http.Response.WriteAsync("data: " + message + "\n\n", http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    finally { ladder.Detach(Key(http), id, listener); }
});
using var timer = new Timer(_ => ladder.Tick(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
app.Run(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ??
    "http://0.0.0.0:" + (Environment.GetEnvironmentVariable("PORT") ?? "25565"));
return 0;

static string Key(HttpContext http)
{
    var value = http.Request.Headers.Authorization.ToString();
    return value.StartsWith("Bearer ", StringComparison.Ordinal) ? value[7..] : "";
}

record Registration(string Name, string? Avatar);
record Credentials(string Name, string Password);
record Ticket(string MatchId, DeckDefinition[] Decks, string[] Names, int Seat, long Seed);
sealed class Member
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Avatar { get; set; }
    public RankProgress Rank { get; set; } = new();
    public List<RankProgress> Previous { get; set; } = [];
    public Dictionary<string, RankedReceipt> Receipts { get; set; } = [];
    public Profile? Collection { get; set; }
    /// <summary>PBKDF2-SHA256 de la contraseña (hex) y su sal; null si la cuenta aún no tiene contraseña.</summary>
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    /// <summary>Veces que el cliente ha detectado el perfil local manipulado; a la segunda se borra la cuenta.</summary>
    public int Strikes { get; set; }
}
record Waiting(Member Player, DeckDefinition Deck, DateTimeOffset At, DateTimeOffset Seen);
sealed class Duel(Member[] players, DeckDefinition[] decks, Season season, long seed, Match match)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public Member[] Players { get; } = players;
    public DeckDefinition[] Decks { get; } = decks;
    public Season Season { get; } = season;
    public long Seed { get; } = seed;
    public Match Engine { get; } = match;
    public string[][] Messages { get; set; } = [[], []];
    public Channel<string>?[] Channels { get; } = [null, null];
    public DateTimeOffset?[] Since { get; } = [DateTimeOffset.UtcNow, DateTimeOffset.UtcNow];
    public DateTimeOffset Heartbeat { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Finished { get; set; }
    public Command? Pending { get; set; }
    public NeedChoice? Choice { get; set; }
    public List<Answer> Answers { get; } = [];
    public int Index(Member p) => Array.IndexOf(Players, p);
    public string Seat(int i) => Engine.State.Players.A.Seat == i + 1 ? P.A : P.B;
    public Ticket Info(int i) => new(Id, Decks, Players.Select(p => p.Name).ToArray(), i + 1, Seed);
}

sealed class Ladder
{
    readonly object gate = new();
    readonly string path;
    readonly RankRules rules;
    readonly Dictionary<string, Member> members;
    readonly List<Waiting> queue = [];
    readonly Dictionary<string, Duel> rooms = [];
    readonly GameContext ctx;
    readonly byte[] sealSecret;
    public Ladder(string directory, RankRules rules)
    {
        var secretPath = Path.Combine(directory, "seal.secret");
        sealSecret = File.Exists(secretPath) ? File.ReadAllBytes(secretPath) : RandomNumberGenerator.GetBytes(32);
        if (!File.Exists(secretPath)) File.WriteAllBytes(secretPath, sealSecret);
        path = Path.Combine(directory, "ladder.json");
        this.rules = rules;
        members = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Member>>(File.ReadAllText(path), Json.Options) ?? [] : [];
        ctx = GameContext.Create(ContentLoader.Load().Cards);
    }

    Season Current => Ranked.SeasonAt(DateTimeOffset.UtcNow, rules);
    static string? Hash(string token) => token.Length == 64 && token.All(Uri.IsHexDigit)
        ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))) : null;
    Member? Get(string key) => Hash(key) is { } digest ? members.GetValueOrDefault(digest) : null;
    static IResult Reply(object? value) => Results.Json(value, Json.Options);
    static IResult Fail(string message, int status = 400) => Results.Json(new { message }, Json.Options, statusCode: status);
    void Save()
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(members, Json.Options));
        File.Move(temp, path, true);
    }
    static bool NameOk(string? name) => name is { Length: >= 3 and <= 24 } && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
    // Perfiles de administración del cliente (solo existen en sus builds de depuración): ningún jugador puede registrar estos nombres.
    static readonly string[] Reserved = ["ACherry", "DevCherry", "MCherry"];
    static bool IsReserved(string name) => Reserved.Contains(name, StringComparer.OrdinalIgnoreCase);
    void NewSeason(Member m, Season season)
    {
        if (m.Rank.Season == season.Id) return;
        if (m.Rank.Season.Length > 0 && m.Rank.Played > 0) m.Previous.Add(m.Rank);
        m.Rank = new(season.Id);
    }
    public IResult Register(string key, Registration form)
    {
        var hash = Hash(key);
        if (hash is null || !NameOk(form.Name)) return Fail("Identidad o nombre inválidos.");
        if (IsReserved(form.Name)) return Fail("Ese nombre está reservado.", 409);
        lock (gate)
        {
            var member = members.GetValueOrDefault(hash);
            if (members.Values.Any(m => m != member && string.Equals(m.Name, form.Name, StringComparison.OrdinalIgnoreCase)))
                return Fail("El nombre competitivo ya está en uso.", 409);
            if (member is null) members[hash] = member = new Member { Id = Guid.NewGuid().ToString("N") };
            member.Name = form.Name;
            member.Avatar = form.Avatar is { } a && Economy.Card(a) is { Token: not true } ? a : null;
            NewSeason(member, Current);
            Save();
            return Reply(new { member.Id });
        }
    }
    // ---------- Cuentas con usuario y contraseña ----------
    // Cada inicio de sesión emite un token nuevo de 64 hex (el mismo formato que RankedKey) y anula el anterior.
    readonly Dictionary<string, (int Fails, DateTimeOffset Until)> attempts = new(StringComparer.OrdinalIgnoreCase);
    const int Iterations = 210_000;

    static bool PasswordOk(string? password) => password is { Length: >= 8 and <= 128 };
    static string Derive(string password, string salt) =>
        Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(salt), Iterations, HashAlgorithmName.SHA256, 32));
    static void SetSecret(Member m, string password)
    {
        m.PasswordSalt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        m.PasswordHash = Derive(password, m.PasswordSalt);
    }
    static bool Verify(Member m, string password) => m.PasswordHash is { } stored && m.PasswordSalt is { } salt &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(stored), Convert.FromHexString(Derive(password, salt)));

    /// <summary>Emite un token nuevo para el miembro y retira el que tenía.</summary>
    string Issue(Member m)
    {
        foreach (var old in members.Where(kv => kv.Value == m).Select(kv => kv.Key).ToList()) members.Remove(old);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        members[Hash(token)!] = m;
        return token;
    }

    public IResult Signup(Credentials form)
    {
        if (!NameOk(form.Name)) return Fail("El nombre debe tener de 3 a 24 letras, números, - o _.");
        if (!PasswordOk(form.Password)) return Fail("La contraseña debe tener de 8 a 128 caracteres.");
        if (IsReserved(form.Name)) return Fail("Ese nombre está reservado.", 409);
        lock (gate)
        {
            if (members.Values.Any(m => string.Equals(m.Name, form.Name, StringComparison.OrdinalIgnoreCase)))
                return Fail("Ese nombre ya está en uso.", 409);
            var member = new Member { Id = Guid.NewGuid().ToString("N"), Name = form.Name };
            SetSecret(member, form.Password);
            NewSeason(member, Current);
            var token = Issue(member);
            Save();
            return Reply(new { member.Id, token, seal = ProfileSeal.KeyFor(sealSecret, member.Id) });
        }
    }

    public IResult Login(Credentials form)
    {
        if (form.Name is null || form.Password is null) return Fail("Faltan usuario o contraseña.");
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (attempts.TryGetValue(form.Name, out var a) && a.Until > now)
                return Fail("Demasiados intentos. Espera un minuto.", 429);
            var member = members.Values.FirstOrDefault(m => string.Equals(m.Name, form.Name, StringComparison.OrdinalIgnoreCase));
            if (member is null || !Verify(member, form.Password))
            {
                int fails = (attempts.TryGetValue(form.Name, out var prev) ? prev.Fails : 0) + 1;
                attempts[form.Name] = (fails, fails >= 5 ? now.AddMinutes(1) : DateTimeOffset.MinValue);
                return Fail("Usuario o contraseña incorrectos.", 401);
            }
            attempts.Remove(form.Name);
            var token = Issue(member);
            Save();
            return Reply(new { member.Id, token, seal = ProfileSeal.KeyFor(sealSecret, member.Id) });
        }
    }

    /// <summary>El cliente avisa de que el perfil local está manipulado: el primer aviso se anota; el segundo borra la cuenta.</summary>
    public IResult Tamper(string key)
    {
        lock (gate)
        {
            if (Get(key) is not { } member) return Fail("Identifica tu perfil.", 401);
            member.Strikes++;
            bool deleted = member.Strikes >= 2;
            if (deleted)
            {
                foreach (var token in members.Where(kv => kv.Value == member).Select(kv => kv.Key).ToList()) members.Remove(token);
                queue.RemoveAll(w => w.Player == member);
            }
            Save();
            return Reply(new { member.Strikes, deleted });
        }
    }

    /// <summary>Pone o cambia la contraseña de la cuenta ya identificada con su token (migra las cuentas antiguas).</summary>
    public IResult SetPassword(string key, Credentials form)
    {
        if (!PasswordOk(form.Password)) return Fail("La contraseña debe tener de 8 a 128 caracteres.");
        lock (gate)
        {
            if (Get(key) is not { } member) return Fail("Identifica tu perfil.", 401);
            SetSecret(member, form.Password);
            Save();
            return Reply(new { member.Id });
        }
    }

    public IResult State(string key)
    {
        lock (gate)
        {
            if (Get(key) is not { } p) return Fail("Vuelve a registrar tu perfil.", 401);
            var season = Current;
            if (p.Rank.Season != season.Id) { NewSeason(p, season); Save(); }
            int waiting = queue.FindIndex(w => w.Player == p);
            if (waiting >= 0) queue[waiting] = queue[waiting] with { Seen = DateTimeOffset.UtcNow };
            var ranked = members.Values.Where(m => m.Rank.Season == season.Id && m.Rank.Played >= rules.Placements).OrderByDescending(m => m.Rank.Ladder)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select((m, index) => new { position = index + 1, entry = new RankedEntry(m.Id, m.Name, m.Avatar, m.Rank) }).ToArray();
            var duel = rooms.Values.FirstOrDefault(r => r.Finished is null && r.Index(p) >= 0);
            return Reply(new { season, rules, rank = p.Rank, id = p.Id, top = ranked.Take(100), mine = ranked.FirstOrDefault(m => m.entry.Id == p.Id),
                ticket = duel?.Info(duel.Index(p)), queued = waiting >= 0,
                previous = p.Previous, receipts = p.Receipts.Values.Reverse().Take(15) });
        }
    }

    public IResult Craft(string key, string cardId)
    {
        lock (gate)
        {
            if (Get(key) is not { } member) return Fail("Identifica tu perfil.", 401);
            var saved = member.Collection ?? new Profile { Name = member.Name };
            try
            {
                var crafted = Economy.Craft(saved, cardId);
                var old = member.Collection;
                member.Collection = crafted;
                try { Save(); }
                catch { member.Collection = old; throw; }
                return Reply(new { cardId, copies = crafted.Has(cardId), essence = crafted.Esencia });
            }
            catch (InvalidOperationException e) { return Fail(e.Message, 409); }
        }
    }
    public IResult Enqueue(string key, DeckDefinition deck)
    {
        lock (gate)
        {
            if (Get(key) is not { } p) return Fail("Falta registrar tu perfil.", 401);
            if (deck is null || !deck.SeparateArcano || deck.Name is not { Length: >= 1 and <= 80 }) return Fail("El mazo competitivo no es válido.");
            var issues = DeckWorkshop.ValidateRanked(deck);
            if (issues.Count > 0) return Fail(issues[0]);
            if (rooms.Values.Any(r => r.Finished is null && r.Index(p) >= 0)) return Reply(new { queued = true });
            var now = DateTimeOffset.UtcNow;
            queue.RemoveAll(w => w.Player == p || now - w.Seen > TimeSpan.FromSeconds(25));
            NewSeason(p, Current);
            var rival = queue.Where(w => w.Player.Rank.Season == p.Rank.Season &&
                    (Math.Abs(w.Player.Rank.Ladder - p.Rank.Ladder) <= 350 || now - w.At > TimeSpan.FromSeconds(35)))
                .OrderBy(w => Math.Abs(w.Player.Rank.Ladder - p.Rank.Ladder)).FirstOrDefault();
            if (rival is null) queue.Add(new(p, deck, now, now));
            else Pair(rival, new Waiting(p, deck, now, now));
            return Reply(new { queued = true });
        }
    }
    void Pair(Waiting a, Waiting b)
    {
        queue.Remove(a);
        queue.Remove(b);
        long seed = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var engine = Match.Start(a.Deck, b.Deck, ctx, seed: seed, online: true);
        var room = new Duel([a.Player, b.Player], [a.Deck, b.Deck], Current, seed, engine);
        rooms[room.Id] = room;
    }
    public IResult Cancel(string key)
    {
        lock (gate)
        {
            if (Get(key) is not { } p) return Fail("Perfil desconocido.", 401);
            queue.RemoveAll(q => q.Player == p);
            if (rooms.Values.FirstOrDefault(r => r.Finished is null && r.Index(p) >= 0) is { } room)
                Finish(room, room.Seat(1 - room.Index(p)));
            return Reply(new { queued = false });
        }
    }
    public IResult Receipt(string key, string id)
    {
        lock (gate)
        {
            var m = Get(key);
            return m?.Receipts.TryGetValue(id, out var receipt) == true ? Reply(receipt) : Fail("El resultado todavía no está disponible.", 404);
        }
    }
    static void Send(Duel room, int i, string message, bool keep = true)
    {
        if (keep) room.Messages[i] = [.. room.Messages[i], message];
        room.Channels[i]?.Writer.TryWrite(message);
    }
    public Channel<string>? Listen(string key, string id)
    {
        lock (gate)
        {
            if (Get(key) is not { } m || !rooms.TryGetValue(id, out var room) || room.Index(m) is < 0) return null;
            var i = room.Index(m);
            room.Channels[i]?.Writer.TryComplete();
            var stream = Channel.CreateUnbounded<string>();
            foreach (var msg in room.Messages[i]) stream.Writer.TryWrite(msg);
            stream.Writer.TryWrite("{\"t\":\"rankTick\"}");
            room.Channels[i] = stream;
            room.Since[i] = null;
            return stream;
        }
    }
    public void Detach(string key, string id, Channel<string> channel)
    {
        lock (gate)
        {
            if (Get(key) is not { } m || !rooms.TryGetValue(id, out var room)) return;
            int i = room.Index(m);
            if (i < 0 || room.Channels[i] != channel) return;
            room.Channels[i] = null;
            room.Since[i] = DateTimeOffset.UtcNow;
            channel.Writer.TryComplete();
        }
    }
    public IResult Receive(string key, string id, JsonObject incoming)
    {
        lock (gate)
        {
            if (Get(key) is not { } m || !rooms.TryGetValue(id, out var room) || room.Index(m) is < 0)
                return Fail("Sala competitiva desconocida.", 404);
            if (room.Finished is not null) return Fail("El duelo ya terminó.", 409);
            int seat = room.Index(m);
            string who = room.Seat(seat);
            try
            {
                var type = (string?)incoming["t"];
                if (type == "rinde") { Finish(room, P.Other(who)); return Reply(new { ok = true }); }
                if (type == "cmd" && room.Pending is null && room.Engine.TurnOf == who)
                {
                    var command = incoming["cmd"]?.Deserialize<Command>(Json.Options);
                    if (command?.Player != who || command.Type == "START_ROUND") return Fail("La orden no pertenece a tu turno.");
                    Apply(room, command, []);
                }
                else if (type == "ans" && room.Pending is { } pending && room.Choice?.Player == who)
                {
                    var raw = incoming["v"];
                    var answer = raw switch
                    {
                        JsonArray a => new Answer(Order: a.Select(n => n!.GetValue<string>()).ToArray()),
                        JsonValue v when v.TryGetValue<bool>(out var yes) => new Answer(Yes: yes),
                        JsonValue v when v.TryGetValue<string>(out var iid) => new Answer(Iid: iid),
                        _ => new Answer(),
                    };
                    Apply(room, pending, [.. room.Answers, answer]);
                }
                else return Fail("La sala está esperando otra decisión.");
                Send(room, 1 - seat, incoming.ToJsonString());
                if (room.Engine.State.Phase == "gameOver") Finish(room, room.Engine.State.Winner ?? "draw");
                return Reply(new { ok = true });
            }
            catch (Exception e) when (e is InvalidOperationException or JsonException or ArgumentException)
            {
                return Fail("La orden no es válida en el estado actual.");
            }
        }
    }
    static void Apply(Duel duel, Command command, List<Answer> options)
    {
        try
        {
            duel.Engine.Send(command, options);
            duel.Pending = null;
            duel.Choice = null;
            duel.Answers.Clear();
            if (duel.Engine.State.Phase == "roundStart")
                duel.Engine.Send(new Command { Type = "START_ROUND" });
        }
        catch (NeedChoice need)
        {
            duel.Pending = command;
            duel.Choice = need;
            duel.Answers.Clear();
            duel.Answers.AddRange(options);
        }
    }
    void Finish(Duel room, string winner)
    {
        if (room.Finished is not null) return;
        room.Finished = DateTimeOffset.UtcNow;
        for (int i = 0; i < 2; i++)
        {
            var m = room.Players[i];
            var outcome = winner == "draw" ? "draw" : room.Seat(i) == winner ? "victory" : "defeat";
            int previousSeason = m.Previous.FindIndex(p => p.Season == room.Season.Id);
            var before = previousSeason >= 0 ? m.Previous[previousSeason] : m.Rank;
            var receipt = Ranked.Resolve(room.Id, before, room.Season, outcome, rules) with
            {
                Result = outcome, Rival = room.Players[1 - i].Name, DeckKey = room.Decks[i].Key,
                DeckName = room.Decks[i].Name, At = room.Finished,
            };
            if (m.Rank.Season == room.Season.Id) m.Rank = receipt.After;
            else if (previousSeason >= 0) m.Previous[previousSeason] = receipt.After;
            else m.Previous.Add(receipt.After);
            m.Receipts[room.Id] = receipt;
            while (m.Receipts.Count > 100) m.Receipts.Remove(m.Receipts.Keys.First());
        }
        Save();
        for (int i = 0; i < 2; i++) Send(room, i, System.Text.Json.JsonSerializer.Serialize(new { t = "rankEnd", winner }));
    }
    public void Tick()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            queue.RemoveAll(w => now - w.Seen > TimeSpan.FromSeconds(25));
            while (queue.Count >= 2 && now - queue[0].At > TimeSpan.FromSeconds(35)) Pair(queue[0], queue[1]);
            foreach (var room in rooms.Values.ToArray())
            {
                if (room.Finished is { } ended)
                {
                    if (now - ended > TimeSpan.FromMinutes(2))
                    {
                        foreach (var chan in room.Channels) chan?.Writer.TryComplete();
                        rooms.Remove(room.Id);
                    }
                    continue;
                }
                var lost = Enumerable.Range(0, 2).Where(i => room.Since[i] is { } since && now - since > TimeSpan.FromSeconds(32)).ToArray();
                if (lost.Length > 0) Finish(room, lost.Length == 2 ? "draw" : room.Seat(1 - lost[0]));
                else if (now - room.Heartbeat > TimeSpan.FromSeconds(12))
                {
                    room.Heartbeat = now;
                    for (int i = 0; i < 2; i++) Send(room, i, "{\"t\":\"rankTick\"}", false);
                }
            }
        }
    }
}
