using System.Text;
using SmartInspectConsole.Core.Packets;
using SmartInspectTui.Formatting;
using Xunit;

namespace SmartInspectTui.Tests;

public class PayloadFormatterTests
{
    [Fact]
    public void Format_AutoDetectsAndIndentsJson()
    {
        var entry = new LogEntry
        {
            Data = Encoding.UTF8.GetBytes("""{"service":"auth","ok":true}"""),
        };

        var result = PayloadFormatter.Format(entry);

        Assert.Equal("JSON", result.DetectedFormat);
        Assert.Contains("\"service\": \"auth\"", result.FormattedText);
        Assert.Contains("\n", result.FormattedText);
    }

    [Fact]
    public void Format_AutoDetectsAndIndentsXml()
    {
        var entry = new LogEntry
        {
            Data = Encoding.UTF8.GetBytes("<root><item id=\"1\">value</item></root>"),
        };

        var result = PayloadFormatter.Format(entry);

        Assert.Equal("XML", result.DetectedFormat);
        Assert.Contains("  <item id=\"1\">value</item>", result.FormattedText);
    }

    [Fact]
    public void Format_AutoDetectsKeyValuePairs()
    {
        var entry = new LogEntry
        {
            Data = Encoding.UTF8.GetBytes("host=localhost\nport=4228\nmode=live"),
        };

        var result = PayloadFormatter.Format(entry);

        Assert.Equal("Key-Value", result.DetectedFormat);
        Assert.Contains("host", result.FormattedText);
        Assert.Contains("= 4228", result.FormattedText);
    }

    [Fact]
    public void Format_AutoDetectsBinaryHex()
    {
        var entry = new LogEntry { Data = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x03, 0x04, 0x05] };

        var result = PayloadFormatter.Format(entry);

        Assert.Equal("Binary", result.DetectedFormat);
        Assert.Contains("00000000  00 01 02 FF FE 03 04 05", result.FormattedText);
    }
}
