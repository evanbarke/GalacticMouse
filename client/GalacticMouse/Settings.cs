using System.Security.Cryptography;
using System.Text.Json;

namespace GalacticMouse;

internal sealed class Settings
{
    public string RelayUrl { get; set; } = "wss://galactamouse.samix-technology.com/ws";
    public string PairingCode { get; set; } = "";
    public bool NeighborRight { get; set; } = true;

    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GalacticMouse", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string NewCode() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] DecodeCode(string code)
    {
        var value = code.Trim().Replace('-', '+').Replace('_', '/');
        value = value.PadRight((value.Length + 3) / 4 * 4, '=');
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length != 32) throw new FormatException("The pairing code must contain 32 random bytes.");
        return bytes;
    }
}
