// Sello de integridad de los perfiles: HMAC-SHA256 del JSON (sin su propio campo "seal") con una clave por cuenta
// que emite el servidor. Detecta quien edita el archivo a mano; no frena a quien extraiga la clave del equipo.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Arcana.Meta;

public enum SealState { Valid, Missing, Broken }

public static class ProfileSeal
{
    /// <summary>Clave de sello de una cuenta: HMAC(secreto del servidor, id de la cuenta).</summary>
    public static string KeyFor(byte[] secret, string memberId) =>
        Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(memberId)));

    // Se firma el JSON re-serializado: cambiar espacios o saltos de línea no cuenta como manipular.
    static string Mac(JsonObject o, string key)
    {
        o.Remove("seal");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(o.ToJsonString())));
    }

    public static string Sign(string json, string key)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        var mac = Mac(o, key);
        o["seal"] = mac;
        return o.ToJsonString();
    }

    public static SealState Check(string json, string key)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return SealState.Broken;
            if (o["seal"] is null) return SealState.Missing;
            var stored = o["seal"]!.GetValue<string>();
            return string.Equals(stored, Mac(o, key), StringComparison.OrdinalIgnoreCase) ? SealState.Valid : SealState.Broken;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return SealState.Broken; }
    }
}

public static class SealTests
{
    public static int Run(Action<bool, string> report)
    {
        int fails = 0;
        void Is(bool ok, string label) { report(ok, label); if (!ok) fails++; }
        var key = ProfileSeal.KeyFor([1, 2, 3], "cuenta-a");
        var signed = ProfileSeal.Sign(Economy.NewProfile("Prueba", "fool").ToJson(), key);
        Is(ProfileSeal.Check(signed, key) == SealState.Valid, "sello: un perfil firmado es válido");
        Is(Economy.Sanitize(signed) is { Name: "Prueba" }, "sello: el campo seal no rompe la carga del perfil");
        var edited = signed.Replace("\"gemas\":0", "\"gemas\":9999");
        Is(edited != signed && ProfileSeal.Check(edited, key) == SealState.Broken, "sello: editar un valor lo invalida");
        Is(ProfileSeal.Check(System.Text.Json.Nodes.JsonNode.Parse(signed)!.ToJsonString(new() { WriteIndented = true }), key) == SealState.Valid, "sello: cambiar solo el formato no cuenta");
        Is(ProfileSeal.Check(signed, ProfileSeal.KeyFor([1, 2, 3], "cuenta-b")) == SealState.Broken, "sello: la clave de otra cuenta no vale");
        Is(ProfileSeal.Check(ProfileSeal.Sign("{\"a\":1}", key).Replace(",\"seal\"", ",\"x\""), key) == SealState.Missing, "sello: sin campo seal se detecta como ausente");
        Is(ProfileSeal.Check("no es json", key) == SealState.Broken, "sello: un archivo ilegible es inválido");
        return fails;
    }
}
