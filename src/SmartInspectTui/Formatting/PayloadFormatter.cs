using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SmartInspectConsole.Core.Packets;

namespace SmartInspectTui.Formatting;

public enum PayloadFormatMode
{
    Auto,
    Text,
    Json,
    Xml,
    BinaryHex,
    KeyValue,
}

public sealed record FormattedPayloadResult(
    string FormattedText,
    string DetectedFormat,
    int ByteSize
);

/// <summary>
/// Auto-detects and pretty-prints SmartInspect <see cref="LogEntry"/> payloads (JSON, XML, Key-Value, Binary Hex, Text).
/// </summary>
public static class PayloadFormatter
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static FormattedPayloadResult Format(
        LogEntry entry,
        PayloadFormatMode mode = PayloadFormatMode.Auto
    )
    {
        var data = entry.Data;
        if (data == null || data.Length == 0)
        {
            return new FormattedPayloadResult("(No payload data)", "Empty", 0);
        }

        var rawText = Encoding.UTF8.GetString(data);
        if (rawText.Length > 0 && rawText[0] == '\uFEFF')
        {
            rawText = rawText[1..];
        }

        return mode switch
        {
            PayloadFormatMode.Text => new FormattedPayloadResult(rawText, "Text", data.Length),
            PayloadFormatMode.Json => TryFormatAsJson(rawText, out var json)
                ? new FormattedPayloadResult(json, "JSON", data.Length)
                : new FormattedPayloadResult(rawText, "JSON (Invalid)", data.Length),
            PayloadFormatMode.Xml => TryFormatAsXml(rawText, out var xml)
                ? new FormattedPayloadResult(xml, "XML", data.Length)
                : new FormattedPayloadResult(rawText, "XML (Invalid)", data.Length),
            PayloadFormatMode.BinaryHex => new FormattedPayloadResult(
                FormatAsBinaryHex(data),
                "Binary",
                data.Length
            ),
            PayloadFormatMode.KeyValue => new FormattedPayloadResult(
                FormatKeyValuePairs(rawText),
                "Key-Value",
                data.Length
            ),
            _ => AutoDetectAndFormat(data, rawText),
        };
    }

    private static FormattedPayloadResult AutoDetectAndFormat(byte[] data, string rawText)
    {
        if (TryFormatAsJson(rawText, out var jsonFormatted))
        {
            return new FormattedPayloadResult(jsonFormatted, "JSON", data.Length);
        }

        if (TryFormatAsXml(rawText, out var xmlFormatted))
        {
            return new FormattedPayloadResult(xmlFormatted, "XML", data.Length);
        }

        if (IsLikelyBinary(data))
        {
            return new FormattedPayloadResult(FormatAsBinaryHex(data), "Binary", data.Length);
        }

        if (IsKeyValuePairs(rawText))
        {
            return new FormattedPayloadResult(
                FormatKeyValuePairs(rawText),
                "Key-Value",
                data.Length
            );
        }

        return new FormattedPayloadResult(rawText, "Text", data.Length);
    }

    public static bool TryFormatAsJson(string text, out string formatted)
    {
        formatted = string.Empty;
        var trimmed = text.Trim();

        if (
            !(
                (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
                || (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            )
        )
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            formatted = JsonSerializer.Serialize(doc.RootElement, IndentedJsonOptions);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryFormatAsXml(string text, out string formatted)
    {
        formatted = string.Empty;
        var trimmed = text.Trim();

        if (!trimmed.StartsWith('<'))
        {
            return false;
        }

        try
        {
            var doc = XDocument.Parse(trimmed);
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                OmitXmlDeclaration = !trimmed.StartsWith(
                    "<?xml",
                    StringComparison.OrdinalIgnoreCase
                ),
            };

            using (var writer = XmlWriter.Create(sb, settings))
            {
                doc.WriteTo(writer);
            }

            formatted = sb.ToString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsLikelyBinary(byte[] data)
    {
        if (data.Length == 0)
        {
            return false;
        }

        var nonPrintableCount = 0;
        var checkLength = Math.Min(data.Length, 1000);

        for (var i = 0; i < checkLength; i++)
        {
            var b = data[i];
            if (b < 32 && b != 9 && b != 10 && b != 13)
            {
                nonPrintableCount++;
            }
            else if (b > 126)
            {
                nonPrintableCount++;
            }
        }

        return (double)nonPrintableCount / checkLength > 0.1;
    }

    public static string FormatAsBinaryHex(byte[] data)
    {
        var sb = new StringBuilder();
        const int bytesPerLine = 16;

        for (var i = 0; i < data.Length; i += bytesPerLine)
        {
            sb.Append($"{i:X8}  ");

            for (var j = 0; j < bytesPerLine; j++)
            {
                if (i + j < data.Length)
                {
                    sb.Append($"{data[i + j]:X2} ");
                }
                else
                {
                    sb.Append("   ");
                }

                if (j == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(' ');

            for (var j = 0; j < bytesPerLine && i + j < data.Length; j++)
            {
                var b = data[i + j];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            sb.AppendLine();

            if (i > 10000)
            {
                sb.AppendLine($"... ({data.Length - i} more bytes)");
                break;
            }
        }

        return sb.ToString();
    }

    public static bool IsKeyValuePairs(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return false;
        }

        var kvCount = 0;
        foreach (var line in lines)
        {
            if (line.Contains('=') || line.Contains(':'))
            {
                kvCount++;
            }
        }

        return (double)kvCount / lines.Length >= 0.5;
    }

    public static string FormatKeyValuePairs(string text)
    {
        var sb = new StringBuilder();
        var lines = text.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            var eqIndex = trimmed.IndexOf('=');
            var colonIndex = trimmed.IndexOf(':');

            var sepIndex = -1;
            if (eqIndex >= 0 && colonIndex >= 0)
            {
                sepIndex = Math.Min(eqIndex, colonIndex);
            }
            else if (eqIndex >= 0)
            {
                sepIndex = eqIndex;
            }
            else if (colonIndex >= 0)
            {
                sepIndex = colonIndex;
            }

            if (sepIndex > 0)
            {
                var key = trimmed[..sepIndex].Trim();
                var value = trimmed[(sepIndex + 1)..].Trim();
                sb.AppendLine($"{key, -20} = {value}");
            }
            else
            {
                sb.AppendLine(trimmed);
            }
        }

        return sb.ToString();
    }
}
