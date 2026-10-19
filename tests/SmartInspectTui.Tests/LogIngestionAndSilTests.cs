using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SmartInspectConsole.Core.Enums;
using SmartInspectConsole.Core.FileIO;
using SmartInspectConsole.Core.Packets;
using SmartInspectTui.Configuration;
using SmartInspectTui.Services;
using Xunit;

namespace SmartInspectTui.Tests;

public class LogIngestionAndSilTests
{
    [Fact]
    public async Task TcpListener_ReceivesLogs_AndSupportsPauseResumeAndClear()
    {
        var port = GetFreeTcpPort();
        var settings = new TuiSettings
        {
            EnableTcp = true,
            TcpPort = port,
            EnablePipe = false,
            EnableWebSocket = false,
            MaxLogEntries = 5000,
        };

        using var service = new LogIngestionService(settings);
        await service.StartListenersAsync();

        try
        {
            await SendTcpLogsAsync(port, "TestApp", ["Message 1", "Message 2"]);
            await WaitUntilAsync(() => service.PendingQueueDepth >= 2);

            var drain1 = service.DrainBatch();
            Assert.True(drain1.HasVisibleChanges);
            Assert.Equal(2, service.FilteredCount);

            // Pause screen and send more messages
            service.SetPaused(true);
            await SendTcpLogsAsync(port, "TestApp", ["Message 3", "Message 4"]);
            await WaitUntilAsync(() => service.PendingQueueDepth >= 2);

            var drainPaused = service.DrainBatch();
            Assert.False(drainPaused.HasVisibleChanges);
            Assert.Equal(2, service.FilteredCount); // Visible screen remains frozen at 2
            Assert.Equal(4, service.RetainedCount); // Retained store has all 4
            Assert.Equal(2, service.PausedBufferedCount);

            // Resume screen -> visible count catches up to 4
            service.SetPaused(false);
            Assert.Equal(4, service.FilteredCount);
            Assert.Equal(0, service.PausedBufferedCount);

            // Clear logs
            service.Clear();
            Assert.Equal(0, service.FilteredCount);
            Assert.Equal(0, service.RetainedCount);
        }
        finally
        {
            await service.StopListenersAsync();
        }
    }

    [Fact]
    public async Task PipeListener_ReceivesLogs_OnLinuxAndCrossPlatform()
    {
        var pipeName = $"si-tui-test-{Guid.NewGuid():N}";
        var settings = new TuiSettings
        {
            EnableTcp = false,
            EnablePipe = true,
            PipeName = pipeName,
            EnableWebSocket = false,
        };

        using var service = new LogIngestionService(settings);
        await service.StartListenersAsync();

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );
            await client.ConnectAsync(5000);
            await ReadLineAsync(client);

            var banner = Encoding.ASCII.GetBytes("PipeTestClient\n");
            await client.WriteAsync(banner);
            await client.FlushAsync();

            var writer = new BinaryPacketWriter();
            await writer.WritePacketAsync(
                client,
                new LogEntry
                {
                    AppName = "PipeApp",
                    SessionName = "Main",
                    Title = "Hello over Unix/Named Pipe",
                    LogEntryType = LogEntryType.Message,
                    Timestamp = DateTime.UtcNow,
                }
            );
            await client.FlushAsync();

            await WaitUntilAsync(() => service.PendingQueueDepth >= 1);
            service.DrainBatch();

            var snapshot = service.GetFilteredSnapshot();
            var single = Assert.Single(snapshot);
            Assert.Equal("PipeApp", single.AppName);
            Assert.Equal("Hello over Unix/Named Pipe", single.Title);
        }
        finally
        {
            await service.StopListenersAsync();
        }
    }

    [Fact]
    public async Task SilFile_SaveAndLoad_RoundTripsEntries()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"si-tui-{Guid.NewGuid():N}.sil");
        try
        {
            var settings = new TuiSettings
            {
                EnableTcp = false,
                EnablePipe = false,
                EnableWebSocket = false,
            };

            using var writerService = new LogIngestionService(settings);
            writerService.EnqueueLogEntry(
                new LogEntry
                {
                    AppName = "RecorderApp",
                    SessionName = "Audit",
                    HostName = "workstation",
                    LogEntryType = LogEntryType.Warning,
                    Title = "Recorded entry 1",
                    Timestamp = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Local),
                    Data = Encoding.UTF8.GetBytes("key=value"),
                }
            );
            writerService.DrainBatch();

            var savedCount = await writerService.SaveSilFileAsync(tempFile);
            Assert.Equal(1, savedCount);

            using var readerService = new LogIngestionService(settings);
            var loadedCount = await readerService.LoadSilFileAsync(tempFile);
            Assert.Equal(1, loadedCount);

            var entry = Assert.Single(readerService.GetFilteredSnapshot());
            Assert.Equal("RecorderApp", entry.AppName);
            Assert.Equal("Audit", entry.SessionName);
            Assert.Equal("Recorded entry 1", entry.Title);
            Assert.Equal("key=value", entry.DataAsString);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static async Task SendTcpLogsAsync(int port, string appName, IEnumerable<string> titles)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();

        await ReadLineAsync(stream);
        var banner = Encoding.ASCII.GetBytes("TcpTestClient\n");
        await stream.WriteAsync(banner);
        await stream.FlushAsync();

        var writer = new BinaryPacketWriter();
        var ack = new byte[2];

        foreach (var title in titles)
        {
            await writer.WritePacketAsync(
                stream,
                new LogEntry
                {
                    AppName = appName,
                    SessionName = "Main",
                    HostName = "localhost",
                    LogEntryType = LogEntryType.Message,
                    Title = title,
                    Timestamp = DateTime.UtcNow,
                }
            );
            await stream.FlushAsync();
            await BinaryPacketReaderReadExactlyAsync(stream, ack);
        }
    }

    private static async Task BinaryPacketReaderReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var readTotal = 0;
        while (readTotal < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(readTotal, buffer.Length - readTotal)
            );
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            readTotal += read;
        }
    }

    private static async Task ReadLineAsync(Stream stream)
    {
        var buf = new byte[1];
        while (await stream.ReadAsync(buf) > 0)
        {
            if (buf[0] == (byte)'\n')
            {
                return;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "Timed out waiting for expected condition.");
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
