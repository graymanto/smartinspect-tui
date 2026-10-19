using System.Text;
using SmartInspectConsole.Core.Enums;
using SmartInspectConsole.Core.Packets;
using SmartInspectTui.Filtering;
using Xunit;

namespace SmartInspectTui.Tests;

public class LogFilterEngineTests
{
    private static List<LogEntry> CreateSampleEntries() =>
        [
            new LogEntry
            {
                AppName = "AuthService",
                SessionName = "Main",
                HostName = "linux-node-01",
                LogEntryType = LogEntryType.Message,
                Title = "User authentication succeeded for alice@example.com",
                ProcessId = 1001,
                ThreadId = 12,
                Data = Encoding.UTF8.GetBytes("""{"userId":"alice","roles":["admin"]}"""),
            },
            new LogEntry
            {
                AppName = "PaymentGateway",
                SessionName = "Stripe",
                HostName = "linux-node-02",
                LogEntryType = LogEntryType.Warning,
                Title = "Upstream response latency exceeded threshold (850ms)",
                ProcessId = 2042,
                ThreadId = 44,
                Data = Encoding.UTF8.GetBytes("endpoint=/v1/charges\nlatencyMs=850"),
            },
            new LogEntry
            {
                AppName = "AuthService",
                SessionName = "Database",
                HostName = "linux-node-01",
                LogEntryType = LogEntryType.Error,
                Title = "Connection timeout while querying PostgreSQL",
                ProcessId = 1001,
                ThreadId = 19,
                Data = Encoding.UTF8.GetBytes("Npgsql.NpgsqlException: Timeout expired"),
            },
            new LogEntry
            {
                AppName = "BackgroundWorker",
                SessionName = "Cleanup",
                HostName = "linux-node-03",
                LogEntryType = LogEntryType.Debug,
                Title = "Verbose heartbeat tick",
                ProcessId = 3099,
                ThreadId = 7,
            },
        ];

    [Fact]
    public void EmptyQuery_ReturnsAllEntries()
    {
        var entries = CreateSampleEntries();
        var engine = new LogFilterEngine(new LogFilterQuery());

        var filtered = engine.FilterEntries(entries);

        Assert.Equal(4, filtered.Count);
    }

    [Fact]
    public void LevelFieldSearch_ForDbgBadge_MatchesDebugEntryOnly()
    {
        var entries = CreateSampleEntries();
        var query = new LogFilterQuery
        {
            ActiveField = LogFilterField.Level,
            SearchText = "DBG",
            EnableFuzzy = false,
        };

        var engine = new LogFilterEngine(query);
        var filtered = engine.FilterEntries(entries);

        var single = Assert.Single(filtered);
        Assert.Equal(LogEntryType.Debug, single.LogEntryType);
        Assert.Equal("BackgroundWorker", single.AppName);
    }

    [Fact]
    public void MultipleFilterClauses_CombineOrWithinSameDiscreteField_AndAcrossDifferentFields()
    {
        var entries = CreateSampleEntries();
        var query = new LogFilterQuery
        {
            Clauses =
            [
                // Match Lvl = INF OR Lvl = ERR
                new LogFilterClause(LogFilterField.Level, "INF"),
                new LogFilterClause(LogFilterField.Level, "ERR"),
                // AND App = AuthService
                new LogFilterClause(LogFilterField.App, "AuthService"),
                // AND NOT Session = Main
                new LogFilterClause(LogFilterField.Session, "Main", Negate: true),
            ],
        };

        var engine = new LogFilterEngine(query);
        var filtered = engine.FilterEntries(entries);

        var single = Assert.Single(filtered);
        Assert.Equal("Database", single.SessionName);
        Assert.Equal(LogEntryType.Error, single.LogEntryType);
    }

    [Fact]
    public void QuickFilters_AppSessionAndMinLevel_FilterCorrectly()
    {
        var entries = CreateSampleEntries();
        var query = new LogFilterQuery
        {
            AppNameFilter = "Auth",
            SessionFilter = "Data",
            MinLogLevel = LogEntryType.Warning,
        };

        var engine = new LogFilterEngine(query);
        var filtered = engine.FilterEntries(entries);

        var single = Assert.Single(filtered);
        Assert.Equal("Connection timeout while querying PostgreSQL", single.Title);
    }

    [Fact]
    public void PropertyTags_MatchSpecificPropertiesAndNegation()
    {
        var entries = CreateSampleEntries();
        var query = new LogFilterQuery
        {
            SearchText = "app:AuthService -session:Main level:Warning+ pid:1001",
            EnableFuzzy = false,
        };

        var engine = new LogFilterEngine(query);
        var filtered = engine.FilterEntries(entries);

        var single = Assert.Single(filtered);
        Assert.Equal("Database", single.SessionName);
    }

    [Fact]
    public void FullTextSearch_MatchesPayloadDataAndProperties()
    {
        var entries = CreateSampleEntries();
        var query = new LogFilterQuery { SearchText = "NpgsqlException", EnableFuzzy = false };

        var engine = new LogFilterEngine(query);
        var filtered = engine.FilterEntries(entries);

        var single = Assert.Single(filtered);
        Assert.Equal(LogEntryType.Error, single.LogEntryType);
    }

    [Fact]
    public void FuzzySearch_MatchesApproximateAndSubsequenceTerms()
    {
        var entries = CreateSampleEntries();

        var query = new LogFilterQuery
        {
            SearchText = "postgre timout",
            EnableFuzzy = true,
            FuzzyThreshold = 75,
        };

        var engine = new LogFilterEngine(query);
        var filtered = engine.FilterEntries(entries);

        var single = Assert.Single(filtered);
        Assert.Equal("Connection timeout while querying PostgreSQL", single.Title);
    }

    [Fact]
    public void AutocompleteIndex_OffersCompletionsForDiscreteFieldsOnly()
    {
        var index = new AutocompleteIndex();
        foreach (var entry in CreateSampleEntries())
        {
            index.IndexEntry(entry);
        }

        // 1. Lvl field autocomplete for "d" -> suggests DBG, Debug, Debug+
        var lvlSuggestions = index.GetSuggestions(LogFilterField.Level, "d");
        Assert.Contains(lvlSuggestions, s => s.CompletedInput == "DBG");

        // 2. App field autocomplete for "Pay" -> suggests PaymentGateway
        var appSuggestions = index.GetSuggestions(LogFilterField.App, "Pay");
        Assert.Contains(appSuggestions, s => s.CompletedInput == "PaymentGateway");

        // 3. Inline "app:Auth" in Any mode -> suggests "app:AuthService"
        var inlineSuggestions = index.GetSuggestions(LogFilterField.Any, "lvl:ERR app:Auth");
        Assert.Contains(inlineSuggestions, s => s.CompletedInput == "lvl:ERR app:AuthService");

        // 4. Title and Data fields do not show autocomplete popups
        Assert.Empty(index.GetSuggestions(LogFilterField.Title, "User"));
        Assert.Empty(index.GetSuggestions(LogFilterField.Data, "userId"));
    }
}
