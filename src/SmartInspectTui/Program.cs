using SmartInspectTui.Configuration;
using SmartInspectTui.Relaying;
using SmartInspectTui.Services;
using SmartInspectTui.UI;
using Terminal.Gui;

namespace SmartInspectTui;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var settings = TuiSettings.Load();
        string? initialSilFile = null;
        var startListeners = true;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;

                case "-p":
                case "--port":
                    if (
                        i + 1 < args.Length
                        && int.TryParse(args[++i], out var tcpPort)
                        && tcpPort is > 0 and <= 65535
                    )
                    {
                        settings.TcpPort = tcpPort;
                        settings.EnableTcp = true;
                    }
                    break;

                case "--no-tcp":
                    settings.EnableTcp = false;
                    break;

                case "--pipe":
                    if (i + 1 < args.Length)
                    {
                        settings.PipeName = args[++i];
                        settings.EnablePipe = true;
                    }
                    break;

                case "--no-pipe":
                    settings.EnablePipe = false;
                    break;

                case "--ws-port":
                    if (
                        i + 1 < args.Length
                        && int.TryParse(args[++i], out var wsPort)
                        && wsPort is > 0 and <= 65535
                    )
                    {
                        settings.WebSocketPort = wsPort;
                        settings.EnableWebSocket = true;
                    }
                    break;

                case "--no-ws":
                    settings.EnableWebSocket = false;
                    break;

                case "--max-entries":
                    if (
                        i + 1 < args.Length
                        && int.TryParse(args[++i], out var maxEntries)
                        && maxEntries >= 1000
                    )
                    {
                        settings.MaxLogEntries = maxEntries;
                    }
                    break;

                case "--no-listen":
                    startListeners = false;
                    break;

                default:
                    if (!arg.StartsWith('-') && initialSilFile == null)
                    {
                        initialSilFile = arg;
                    }
                    break;
            }
        }

        await using var relaySink = new NoOpOtelRelaySink
        {
            IsEnabled = settings.OtelRelay.Enabled,
        };
        using var ingestionService = new LogIngestionService(settings, relaySink);

        if (!string.IsNullOrWhiteSpace(initialSilFile))
        {
            if (!File.Exists(initialSilFile))
            {
                Console.Error.WriteLine($"Error: .sil file not found: {initialSilFile}");
                return 1;
            }

            await ingestionService.LoadSilFileAsync(initialSilFile);
        }

        if (startListeners)
        {
            await ingestionService.StartListenersAsync();
        }

        Application.Init();
        try
        {
            var top = new MainWindow(settings, ingestionService);
            top.StartUiTimer();
            try
            {
                Application.Run(top);
            }
            finally
            {
                top.StopUiTimer();
            }
        }
        finally
        {
            Application.Shutdown();
            await ingestionService.StopListenersAsync();
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            SmartInspect TUI - Cross-platform terminal console for SmartInspect logs

            Usage:
              smartinspect-tui [options] [file.sil]

            Options:
              -p, --port <port>       TCP listener port (default: 4228)
              --no-tcp                Disable TCP listener
              --pipe <name>           Named pipe listener name (default: smartinspect)
              --no-pipe               Disable named pipe listener
              --ws-port <port>        WebSocket listener port (default: 4229)
              --no-ws                 Disable WebSocket listener
              --max-entries <count>   Maximum retained log entries (default: 20000)
              --no-listen             Do not start live listeners automatically on launch
              -h, --help              Show this help message
            """
        );
    }
}
