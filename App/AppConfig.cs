using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NmosUmd.Tsl;

namespace NmosUmd.App;

/// <summary>Everything the window remembers between runs.</summary>
public sealed class AppConfig
{
    // ---- registry ----

    /// <summary>True when the registry address came from the mDNS list rather than being typed.</summary>
    public bool UseDiscovery { get; set; } = true;

    /// <summary>Registry address as "host" or "host:port"; also accepts a full URL.</summary>
    public string RegistryAddress { get; set; } = string.Empty;

    public bool RegistryHttps { get; set; }
    public bool AllowInvalidCertificates { get; set; }

    /// <summary>Blank means "use the highest version the registry advertises".</summary>
    public string ApiVersion { get; set; } = string.Empty;

    public int PollIntervalMs { get; set; } = 1000;

    // ---- TSL output ----

    public string TslHost { get; set; } = "127.0.0.1";
    public int TslPort { get; set; } = 8900;
    public bool UseTcp { get; set; }
    public bool StreamFraming { get; set; }
    public TslVersion Version { get; set; } = TslVersion.V50;
    public int Screen { get; set; }
    public bool ScreenBroadcast { get; set; }
    public bool Unicode { get; set; }
    /// <summary>
    /// How often each display is re-sent its current label. A change is sent as soon as it is
    /// seen, so this only governs the keepalive that recovers a display which was powered down
    /// or a receiver that reconnected.
    /// </summary>
    public int SendIntervalMs { get; set; } = 5000;

    /// <summary>Connect to the registry and start the output as soon as the window opens.</summary>
    public bool AutoStart { get; set; } = true;

    public TallyColour RoutedLamp { get; set; } = TallyColour.Off;
    public TallyColour UnroutedLamp { get; set; } = TallyColour.Off;
    public bool DriveTextTally { get; set; }
    public int Brightness { get; set; } = 3;

    // ---- label ----

    public string RoutedTemplate { get; set; } = LabelFormatter.DefaultRoutedTemplate;
    public string UnroutedTemplate { get; set; } = LabelFormatter.DefaultUnroutedTemplate;
    public FitMode FitMode { get; set; } = FitMode.Truncate;
    public bool Uppercase { get; set; }

    // ---- mapping ----

    public List<Assignment> Assignments { get; set; } = new();
}

/// <summary>Loads and saves <see cref="AppConfig"/> as JSON.</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// %APPDATA%\NmosUmd\config.json - beside the executable would be tidier, but the exe often
    /// lives somewhere read-only such as a network share or Program Files.
    /// </summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NmosUmd", "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
    }
}
