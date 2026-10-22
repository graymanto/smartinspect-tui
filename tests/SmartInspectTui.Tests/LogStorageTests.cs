using System.Text;
using SmartInspectConsole.Core.Enums;
using SmartInspectConsole.Core.FileIO;
using SmartInspectConsole.Core.Packets;
using SmartInspectTui.Configuration;
using SmartInspectTui.Filtering;
using SmartInspectTui.Formatting;
using SmartInspectTui.Services;
using Xunit;

namespace SmartInspectTui.Tests;

public sealed class LogStorageTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static TuiSettings Settings() =>
        new()
        {
            EnableTcp = false,
            EnablePipe = false,
            EnableWebSocket = false,
            StorageDirectory = Path.Combine(Path.GetTempPath(), "si-storage-tests"),
            MaxDiskBytes = 16L * 1024 * 1024,
            PageSize = 20,
        };

    private static LogEntry Entry(int i, int bytes = 0) =>
        new()
        {
            Title = $"message {i:D6}",
            AppName = "test",
            SessionName = "main",
            HostName = "host",
            Timestamp = new DateTime(2026, 1, 1).AddMilliseconds(i),
            LogEntryType = LogEntryType.Message,
            Data = bytes == 0 ? null : new byte[bytes],
        };

    private static void Drain(LogIngestionService service)
    {
        while (service.PendingQueueDepth > 0)
            service.DrainBatch();
    }

    [Fact]
    public void Queue_IsBoundedByBytesAndCount_AndKeepsNewest()
    {
        var settings = Settings();
        settings.MaxPendingBytes = 10_000;
        settings.MaxPendingEntries = 4;
        using var service = new LogIngestionService(settings);
        for (var i = 0; i < 100; i++)
            service.EnqueueLogEntry(Entry(i, 1000));
        Assert.InRange(service.PendingBytes, 1, settings.MaxPendingBytes);
        Assert.InRange(service.PendingQueueDepth, 1, 4);
        Assert.True(service.DroppedCount >= 96);
        Drain(service);
        Assert.Equal(0, service.PendingBytes);
        Assert.Equal("message 000099", service.GetPage(tail: true).Entries.Last().Title);
        service.EnqueueLogEntry(Entry(100, 20_000));
        Assert.Equal(0, service.PendingQueueDepth);
    }

    [Fact]
    public void History_PagesSummaries_LoadsPayloadOnDemand_AndExpiresPausedEntries()
    {
        var settings = Settings();
        settings.MaxLogEntries = 60;
        using var service = new LogIngestionService(settings);
        for (var i = 0; i < 50; i++)
            service.EnqueueLogEntry(Entry(i, 1000));
        Drain(service);
        var first = service.GetPage();
        Assert.Equal(20, first.Entries.Count);
        Assert.All(first.Entries, e => Assert.Null(e.Data));
        var id = first.Entries[0].SequenceId;
        Assert.Equal(1000, service.GetEntry(id)!.Data!.Length);
        service.SetPaused(true);
        for (var i = 50; i < 250; i++)
            service.EnqueueLogEntry(Entry(i, 1000));
        Drain(service);
        Assert.InRange(service.RetainedCount, 1, 60);
        Assert.Null(service.GetEntry(id));
        Assert.Empty(service.GetPage().Entries);
        Assert.True(service.EvictedCount > 0);
        service.SetPaused(false);
        Assert.Equal(service.RetainedCount, service.FilteredCount);
        var tail = service.GetPage(tail: true);
        Assert.Equal("message 000249", tail.Entries.Last().Title);
    }

    [Fact]
    public async Task HistoricalFilters_PreserveExistingSemantics_IncludingUnicodeAndPayload()
    {
        using var service = new LogIngestionService(Settings());
        var entries = new[]
        {
            new LogEntry
            {
                Title = "Hello WORLD",
                AppName = "AuthService",
                Data = Encoding.UTF8.GetBytes("secret=foo"),
            },
            new LogEntry
            {
                Title = "Héllo world",
                AppName = "Gateway",
                Data = Encoding.UTF8.GetBytes("secret=bar"),
            },
            new LogEntry { Title = "quote \"world\" and OR", AppName = "AuthService" },
            new LogEntry { Title = "different", LogEntryType = LogEntryType.Warning },
        };
        foreach (var entry in entries)
            service.EnqueueLogEntry(entry);
        Drain(service);
        var queries = new[]
        {
            new LogFilterQuery
            {
                ActiveField = LogFilterField.Title,
                SearchText = "WORLD",
                EnableFuzzy = false,
            },
            new LogFilterQuery
            {
                ActiveField = LogFilterField.Title,
                SearchText = "hello,different",
                EnableFuzzy = false,
            },
            new LogFilterQuery
            {
                ActiveField = LogFilterField.Title,
                SearchText = "\"world\"",
                EnableFuzzy = false,
            },
            new LogFilterQuery { SearchText = "data:bar -app:Auth", EnableFuzzy = false },
            new LogFilterQuery
            {
                SearchText = "app:Auth app:Gateway -title:different",
                EnableFuzzy = false,
            },
            new LogFilterQuery
            {
                ActiveField = LogFilterField.Title,
                SearchText = "hlwrld",
                EnableFuzzy = true,
            },
            new LogFilterQuery { SearchText = "level:Warning+", EnableFuzzy = false },
        };
        foreach (var query in queries)
        {
            await service.UpdateFilterAsync(query);
            var expected = entries
                .Where(new LogFilterEngine(query).Matches)
                .Select(e => e.Title)
                .ToArray();
            Assert.Equal(expected, service.GetPage().Entries.Select(e => e.Title));
            Assert.Equal(expected.Length, service.FilteredCount);
        }
    }

    [Fact]
    public async Task CancelledAndSupersededSearches_DoNotReplaceLatestResults()
    {
        using var service = new LogIngestionService(Settings());
        for (var i = 0; i < 500; i++)
            service.EnqueueLogEntry(Entry(i));
        Drain(service);
        var oldSearch = service.UpdateFilterAsync(
            new LogFilterQuery { SearchText = "absent", EnableFuzzy = true }
        );
        var latest = service.UpdateFilterAsync(
            new LogFilterQuery
            {
                ActiveField = LogFilterField.Title,
                SearchText = "000499",
                EnableFuzzy = false,
            }
        );
        try
        {
            await oldSearch;
        }
        catch (OperationCanceledException) { }
        await latest;
        Assert.Equal("message 000499", Assert.Single(service.GetPage().Entries).Title);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpdateFilterAsync(new(), cancellation.Token)
        );
        Assert.Equal(1, service.FilteredCount);
    }

    [Fact]
    public void DiskQuota_RotatesOldHistory_WithoutGrowingPastQuota()
    {
        var settings = Settings();
        settings.MaxDiskBytes = 2 * 1024 * 1024;
        using var service = new LogIngestionService(settings);
        for (var i = 0; i < 300; i++)
        {
            service.EnqueueLogEntry(Entry(i, 32 * 1024));
            Drain(service);
        }
        Assert.True(service.EvictedCount > 0);
        Assert.InRange(new FileInfo(service.DatabasePath).Length, 1, settings.MaxDiskBytes);
        Assert.Equal("message 000299", service.GetPage(tail: true).Entries.Last().Title);
        Assert.Equal(0, service.DroppedCount);
    }

    [Fact]
    public void EntryTooLargeForDiskQuota_DoesNotEraseExistingHistory()
    {
        var settings = Settings();
        settings.MaxDiskBytes = 2 * 1024 * 1024;
        using var service = new LogIngestionService(settings);
        service.EnqueueLogEntry(Entry(1));
        Drain(service);
        service.EnqueueLogEntry(Entry(2, 1024 * 1024));
        Drain(service);
        Assert.Equal(1, service.DroppedCount);
        Assert.Equal("message 000001", Assert.Single(service.GetPage().Entries).Title);
        service.EnqueueLogEntry(Entry(3));
        Drain(service);
        Assert.Equal(2, service.RetainedCount);
    }

    [Fact]
    public async Task ImportExport_StreamAllHistoryBeyondTheDisplayPage()
    {
        var settings = Settings();
        var file = Path.Combine(Path.GetTempPath(), $"si-stream-{Guid.NewGuid():N}.sil");
        var filtered = file + ".filtered.sil";
        try
        {
            using var service = new LogIngestionService(settings);
            for (var i = 0; i < 75; i++)
                service.EnqueueLogEntry(Entry(i, 50));
            Drain(service);
            Assert.Equal(20, service.GetPage().Entries.Count);
            Assert.Equal(75, await service.SaveSilFileAsync(file));
            await service.UpdateFilterAsync(
                new LogFilterQuery
                {
                    ActiveField = LogFilterField.Title,
                    SearchText = "000074",
                    EnableFuzzy = false,
                }
            );
            Assert.Equal(1, await service.SaveSilFileAsync(filtered, true));
            using var loaded = new LogIngestionService(settings);
            Assert.Equal(75, await loaded.LoadSilFileAsync(file));
            Assert.Equal(75, loaded.RetainedCount);
            Assert.Equal(
                50,
                loaded.GetEntry(loaded.GetPage(tail: true).Entries.Last().SequenceId)!.Data!.Length
            );
            var saved = await new SilFileReader().ReadFileAsync(filtered);
            Assert.Equal("message 000074", Assert.IsType<LogEntry>(Assert.Single(saved)).Title);
        }
        finally
        {
            File.Delete(file);
            File.Delete(filtered);
        }
    }

    [Fact]
    public async Task BackgroundWriter_CapturesWhilePaused_AndDisposesItsCache()
    {
        var service = new LogIngestionService(Settings());
        var path = service.DatabasePath;
        try
        {
            service.StartProcessing();
            service.SetPaused(true);
            for (var i = 0; i < 30; i++)
                service.EnqueueLogEntry(Entry(i));
            for (var i = 0; i < 200 && service.RetainedCount < 30; i++)
                await Task.Delay(10);
            Assert.Equal(30, service.RetainedCount);
            Assert.Equal(0, service.FilteredCount);
            service.SetPaused(false);
            Assert.Equal(30, service.FilteredCount);
        }
        finally
        {
            service.Dispose();
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WebSocket_RejectsOversizedFragmentedMessagesBeforeParsing()
    {
        using var reservation = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            0
        );
        reservation.Start();
        var port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new SmartInspectConsole.Core.Listeners.SmartInspectWebSocketListener(
            port
        );
        var error = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        listener.Error += (_, ex) => error.TrySetResult(ex);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await listener.StartAsync(timeout.Token);
        using var client = new System.Net.WebSockets.ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/"), timeout.Token);
        var fragment = new byte[1024 * 1024];
        for (var i = 0; i < 16; i++)
            await client.SendAsync(
                fragment,
                System.Net.WebSockets.WebSocketMessageType.Text,
                false,
                timeout.Token
            );
        await client.SendAsync(
            new byte[1],
            System.Net.WebSockets.WebSocketMessageType.Text,
            false,
            timeout.Token
        );
        Assert.IsType<InvalidDataException>(await error.Task.WaitAsync(timeout.Token));
        var response = await client.ReceiveAsync(
            new ArraySegment<byte>(new byte[1024]),
            timeout.Token
        );
        Assert.Equal(
            System.Net.WebSockets.WebSocketCloseStatus.MessageTooBig,
            response.CloseStatus
        );
        await listener.StopAsync();
    }

    [Fact]
    public void SustainedCapture_DoesNotKeepLogObjectsAlive_AndKeepsPagesAndDiskBounded()
    {
        var settings = Settings();
        settings.MaxDiskBytes = 8 * 1024 * 1024;
        using var service = new LogIngestionService(settings);
        static WeakReference[] Capture(LogIngestionService target, int start)
        {
            var references = new WeakReference[1000];
            for (var i = 0; i < 1000; i++)
            {
                var entry = Entry(start + i, 1024);
                references[i] = new WeakReference(entry);
                target.EnqueueLogEntry(entry);
            }
            Drain(target);
            return references;
        }
        var firstBatch = Capture(service, 0);
        for (var batch = 1; batch < 100; batch++)
        {
            Capture(service, batch * 1000);
            Assert.InRange(service.GetPage(tail: true).Entries.Count, 1, settings.PageSize);
            Assert.InRange(new FileInfo(service.DatabasePath).Length, 1, settings.MaxDiskBytes);
            if (batch is 24 or 49 or 99)
            {
                var managed = GC.GetTotalMemory(true);
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                output.WriteLine(
                    $"{(batch + 1) * 1000:N0} entries: managed={managed / 1048576.0:F1} MiB, RSS={process.WorkingSet64 / 1048576.0:F1} MiB, retained={service.RetainedCount}"
                );
            }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(firstBatch, reference => Assert.False(reference.IsAlive));
        Assert.Equal(100_000, service.TotalReceived);
        Assert.Equal(0, service.DroppedCount);
        Assert.True(service.EvictedCount > 90_000);
        Assert.Equal("message 099999", service.GetPage(tail: true).Entries.Last().Title);
    }

    [Fact]
    public void PayloadPreview_IsBounded_WithoutTruncatingStoredData()
    {
        var entry = Entry(1, 1024 * 1024);
        var preview = PayloadFormatter.FormatPreview(entry, PayloadFormatMode.Text);
        Assert.Equal(1024 * 1024, preview.ByteSize);
        Assert.True(preview.FormattedText.Length < 70_000);
        Assert.Equal(1024 * 1024, entry.Data!.Length);
    }
}
