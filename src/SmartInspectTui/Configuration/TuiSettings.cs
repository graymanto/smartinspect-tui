using System.Text.Json;
using SmartInspectConsole.Core.Listeners;

namespace SmartInspectTui.Configuration;

public sealed class OtelRelayOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://localhost:4317";
    public string Protocol { get; set; } = "grpc";
    public string DefaultServiceName { get; set; } = "smartinspect-relay";
}

public sealed class TuiSettings
{
    public int TcpPort { get; set; } = SmartInspectTcpListener.DefaultPort;
    public bool EnableTcp { get; set; } = true;

    public string PipeName { get; set; } = SmartInspectPipeListener.DefaultPipeName;
    public bool EnablePipe { get; set; } = true;

    public int WebSocketPort { get; set; } = SmartInspectWebSocketListener.DefaultPort;
    public bool EnableWebSocket { get; set; } = true;

    public int MaxLogEntries { get; set; } = 2_000_000;
    public long MaxPendingBytes { get; set; } = 16L * 1024 * 1024;
    public int MaxPendingEntries { get; set; } = 10_000;
    public long MaxDiskBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int PageSize { get; set; } = 500;
    public string? StorageDirectory { get; set; }
    public bool AutoScroll { get; set; } = true;
    public bool ShowDetailsPane { get; set; } = true;
    public bool EnableFuzzySearch { get; set; } = true;

    public OtelRelayOptions OtelRelay { get; set; } = new();

    public static string GetDefaultSettingsPath()
    {
        var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(configDir))
        {
            configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config"
            );
        }

        return Path.Combine(configDir, "smartinspect-tui", "settings.json");
    }

    public static TuiSettings Load(string? path = null)
    {
        var targetPath = path ?? GetDefaultSettingsPath();
        try
        {
            if (File.Exists(targetPath))
            {
                var json = File.ReadAllText(targetPath);
                var loaded = JsonSerializer.Deserialize<TuiSettings>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
                if (loaded != null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to defaults if settings file cannot be read
        }

        return new TuiSettings();
    }

    public void Save(string? path = null)
    {
        var targetPath = path ?? GetDefaultSettingsPath();
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(
                this,
                new JsonSerializerOptions { WriteIndented = true }
            );
            File.WriteAllText(targetPath, json);
        }
        catch
        {
            // Ignore write errors on read-only environments
        }
    }
}
