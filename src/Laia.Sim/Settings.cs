using System.Text.Json;

namespace Laia.Sim;

/// <summary>How the push-to-talk button is bound: a keyboard/mouse key or a joystick button.</summary>
public sealed record PttBinding(string Kind, int Code, int Device = 0)
{
    public string Describe() => Kind switch
    {
        "joy" => $"Controller {Device + 1}, button {Code + 1}",
        _ => KeyName(Code),
    };

    private static string KeyName(int vk) => vk switch
    {
        0x04 => "Middle mouse button",
        0x05 => "Mouse button 4",
        0x06 => "Mouse button 5",
        0xA0 => "Left Shift", 0xA1 => "Right Shift",
        0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl",
        0xA4 => "Left Alt", 0xA5 => "Right Alt",
        0x20 => "Space bar",
        0xC0 => "` (tilde key)",
        _ => ((Keys)vk).ToString(),
    };
}

public sealed class SimSettings
{
    public PttBinding? Ptt { get; set; }
    public string? PreferredServer { get; set; }
    public bool ShowTranscript { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public string? MicDeviceId { get; set; }
    public string? SpeakerDeviceId { get; set; }
    public int Volume { get; set; } = 90;
    public bool RadioClicks { get; set; } = true;

    private static string PathOnDisk => System.IO.Path.Combine(Log.Dir, "sim-settings.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static SimSettings Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<SimSettings>(File.ReadAllText(PathOnDisk), Opts) ?? new SimSettings();
        }
        catch (Exception ex) { Log.Write("Settings unreadable: " + ex.Message); }
        return new SimSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, Opts));
        }
        catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
    }
}
