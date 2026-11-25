// Textos e identidad de cada Arcano (port de Menu.tsx / ReplayViewer.tsx: INFO, ESTILO, LEMA, TEMA, DECK, MAJOR, DERROTA).
namespace Arcana.Meta;

public sealed record DeckLore(string Name, string Style, string Motto, string Synergy, string HowToPlay, string Color, float PackHue, string Defeat);

public static class Lore
{
    public static readonly IReadOnlyDictionary<string, string> Major = new Dictionary<string, string>
    {
        ["fool"] = "F00", ["magician"] = "M00", ["priestess"] = "P00", ["death"] = "D00", ["devil"] = "X00",
        ["lovers"] = "L00", ["emperor"] = "E00", ["empress"] = "Z00", ["fortune"] = "R00", ["chariot"] = "C00",
    };

    public static readonly IReadOnlyDictionary<string, DeckLore> Decks = new Dictionary<string, DeckLore>
    {
        ["fool"] = new("El Loco", "Tempo agresivo", "Roba, descarta y ataca sin pedir permiso.",
            "Robar y descartar: cada carta robada hace crecer al Bufón, cada descarte refuerza a la Peregrina, y El Loco convierte cartas muertas en cartas nuevas.",
            "Tempo agresivo. Baja Unidades baratas en ATQ, ataca cada turno (El Loco golpea directo aunque haya Unidades) y guarda Trucos de combate (Apuesta Temeraria, Santo Grial) para ganar los enfrentamientos.",
            "#b98cff", 90, "Bah… caer también es parte del viaje. La próxima vez saltaré más lejos."),
        ["magician"] = new("El Mago", "Control de Hechizos", "Hechizos en cadena y una negación en el momento justo.",
            "Hechizos en campo: el Guardián gana ATQ por cada Hechizo, el Círculo de Potencia sube a todas tus Unidades y El Mago abarata el siguiente.",
            "Medio juego. Mantén dos o tres Hechizos activos sobre cuerpos sólidos y reserva energía para negar las jugadas clave del rival (Contraconjuro, Sello).",
            "#ff7a4a", 200, "Mis cálculos fallaron… pero cada error es un hechizo nuevo por aprender."),
        ["priestess"] = new("La Sacerdotisa", "Muro y curación", "Ordena tu destino, aguanta y cúrate.",
            "Presagio y curación: ordenas tus robos, pagas PV por ventaja y los recuperas; cada curación hace crecer a La Sacerdotisa.",
            "Muro y desgaste. Unidades de DEF alta en defensa hacen que atacar le cueste al rival; gana a largo plazo eligiendo siempre tu mejor robo.",
            "#4db8ff", 20, "Las estrellas ya lo sabían… y aun así no lo vi venir."),
        ["death"] = new("La Muerte", "Sacrificio y control", "Sacrifica lo pequeño para destruir lo grande.",
            "Sacrificio: tus Unidades baratas son combustible para destruir amenazas y robar; el Rito de Despedida convierte cada baja en ATQ.",
            "Control. Cambia fichas pequeñas por las piezas grandes del rival y cierra con La Muerte y el Caballero del Umbral dominando el campo.",
            "#c9d6e8", 0, "Todo final es solo un umbral. Volveré a cruzarlo."),
        ["devil"] = new("El Diablo", "Carrera de daño", "Tu vida es un recurso: quémala antes que la del rival.",
            "PV como recurso: pagar vida potencia a tus atacantes y cada sacrificio propio quema los PV del rival.",
            "Carrera de daño. Presiona desde la primera ronda y calcula: ganas si el rival llega a 0 antes de que tus propios pagos te alcancen.",
            "#ff3b5c", 170, "Esta deuda… te la cobraré con intereses."),
        ["lovers"] = new("Los Enamorados", "Parejas", "Parejas inseparables que golpean dos veces.",
            "Acoplar: desde la mano, paga su coste y acopla la carta sobre su pareja exacta en tu campo (sin espacio extra). La Unidad emparejada usa el mayor ATQ y la mayor DEF de ambas y tiene los efectos de las dos; si deja el campo, ambas van al cementerio. Al emparejar: efecto que se activa una vez al acoplar. Los Enamorados potencian a las Unidades emparejadas.",
            "Medio juego. Baja las parejas juntas (Escudero y Doncella, Bardo y Bailarina, Caballero y Dama) y protégelas con Promesa de Protección.",
            "#ff7eb6", 110, "Un corazón roto late más fuerte la próxima vez."),
        ["emperor"] = new("El Emperador", "Formación", "Orden y jerarquía: un ejército que crece en formación.",
            "Jerarquía: ordena tus Unidades de mayor a menor coste (de izquierda a derecha) y todo tu ejército se fortalece; el Líder manda.",
            "Estructura y fuerza planeada. Invoca primero lo caro y luego lo barato; si el orden se rompe, El Emperador u Orden de Batalla lo restauran.",
            "#ffb347", 175, "Un imperio no cae en una batalla… se reordena."),
        ["empress"] = new("La Emperatriz", "Enjambre", "Cada baja alimenta a la colmena.",
            "Enjambre: invoca Retoños sin parar; cada baja barata trae más cuerpos y La Emperatriz hace más fuertes, para siempre, a las Unidades menores de tu campo.",
            "Ancho y molesto. Llena el campo, cambia cada Retoño por valor y remata con el Acechador y el Frenesí de la Colmena.",
            "#7ee081", 260, "Siempre nacerán más. Siempre."),
        ["fortune"] = new("La Rueda de la Fortuna", "Azar", "Dados y monedas: la suerte también se juega.",
            "Azar: dados y monedas deciden estadísticas, curaciones y daño; la Moneda Trucada castiga cada tirada alta.",
            "Apuesta. Tus Unidades salen con estadísticas al dado y la Ruleta del Destino quema PV; arriesga cuando vas detrás.",
            "#5ce1e6", 325, "La rueda gira… y mañana estará de mi lado."),
        ["chariot"] = new("El Carro", "Carga conjunta", "Monstruos de bronce que atacan juntos.",
            "Carga: cada ataque empuja a los demás. El Escudero y la Carga Unida suman ATQ al atacante, el Lancero crece con cada aliado y El Carro arrolla a las Unidades en DEF.",
            "Presión con cuerpos grandes. Llena el campo, declara los ataques en cadena y guarda la Carga Unida para el golpe que rompe la defensa.",
            "#6c8cff", 230, "Las ruedas se detienen… solo para tomar impulso."),
    };

    /// <summary>Bienvenida de una cuenta nueva. Los «ecos» son las cartas de campaña del modo historia.</summary>
    public const string Welcome = "Bienvenido, aventurero, a Arcana. Hubo un tiempo en que los diez Arcanos Mayores gobernaban el mundo. " +
        "Hoy sus ecos vagan como cartas sueltas. Tú has sido llamado para reunirlas. Pero antes, uno de ellos debe elegirte.";

    /// <summary>Elección del primer Arcano: mazo inicial → (frase, dificultad).</summary>
    public static readonly IReadOnlyDictionary<string, (string Phrase, string Difficulty)> Starters = new Dictionary<string, (string, string)>
    {
        ["starter-fool"] = ("Cada paso es una apuesta", "Alta"),
        ["starter-death"] = ("Todo cae, y lo que cae da algo", "Media"),
        ["starter-lovers"] = ("Juntos, nada nos detiene", "Fácil"),
    };

    public static string DeckName(string id) => Decks.TryGetValue(id, out var l) ? l.Name : id == "guia" ? "Aprendiz del Velo" : id;
    public static string Color(string id) => Decks.TryGetValue(id, out var l) ? l.Color : "#e2bd70";

    public static string Roman(int n)
    {
        if (n == 0) return "0";
        var r = "";
        foreach (var (v, t) in new[] { (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
            while (n >= v) { r += t; n -= v; }
        return r;
    }

    public static readonly IReadOnlyDictionary<string, (string Name, string Color)> Rarities = new Dictionary<string, (string, string)>
    {
        ["common"] = ("Común", "#b8c0cc"), ["rare"] = ("Rara", "#4da3ff"), ["epic"] = ("Épica", "#b565ff"), ["legendary"] = ("Legendaria", "#ffb52e"),
    };
    public static readonly string[] RarityOrder = ["common", "rare", "epic", "legendary"];

    public static readonly IReadOnlyDictionary<string, string> TypeNames = new Dictionary<string, string> { ["unit"] = "Unidades", ["spell"] = "Hechizos", ["trick"] = "Trucos" };
    public static readonly IReadOnlyDictionary<string, string> TypeName = new Dictionary<string, string> { ["unit"] = "Unidad", ["spell"] = "Hechizo", ["trick"] = "Truco" };
    public static readonly IReadOnlyDictionary<string, string> SchoolColor = new Dictionary<string, string>
    {
        ["wands"] = "#ff8a3c", ["cups"] = "#4db8ff", ["swords"] = "#c9d6ff", ["pentacles"] = "#7be07b",
    };
    public static readonly IReadOnlyDictionary<string, string> SchoolName = new Dictionary<string, string>
    {
        ["wands"] = "Bastos", ["cups"] = "Copas", ["swords"] = "Espadas", ["pentacles"] = "Oros",
    };
}
