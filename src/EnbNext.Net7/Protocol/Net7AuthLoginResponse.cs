using System.Collections.ObjectModel;
using System.Text;

namespace EnbNext.Net7.Protocol;

/// <summary>
/// Exact current AuthLogin response-object projection needed by the integrated
/// login path. Parsing follows: LF records, the four-byte edge trim
/// set, separator-class priority, case-sensitive keys and first-key-wins.
/// </summary>
public sealed record Net7AuthLoginResponse(
    bool IsValid,
    string RawValid,
    string Ticket,
    string ReasonCode,
    string ReasonText,
    string ReasonUrl,
    IReadOnlyDictionary<string, string> Fields)
{
    private static readonly char[] TrimChars = [' ', '\r', '\t', '\n'];
    private static readonly char[] SeparatorPriority = ['=', ' ', '\t', ','];

    public static Net7AuthLoginResponse Decode(ReadOnlySpan<byte> body)
    {
        // Latin-1 gives a one-byte-to-one-char projection. AuthLogin's parser is
        // byte-oriented and does not apply a first-NUL truncation rule.
        string text = Encoding.Latin1.GetString(body);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string rawRecord in text.Split('\n'))
        {
            string record = Trim(rawRecord);
            if (record.Length == 0 || record[0] == '#')
                continue;

            int separatorIndex = -1;
            foreach (char separator in SeparatorPriority)
            {
                separatorIndex = record.IndexOf(separator);
                if (separatorIndex >= 0)
                    break;
            }

            if (separatorIndex < 0)
                continue;

            string key = Trim(record[..separatorIndex]);
            string value = Trim(record[(separatorIndex + 1)..]);

            // The current map is unique-key / first exact duplicate wins.
            fields.TryAdd(key, value);
        }

        fields.TryGetValue("Valid", out string? rawValid);
        fields.TryGetValue("Ticket", out string? ticket);
        fields.TryGetValue("reasoncode", out string? reasonCode);
        fields.TryGetValue("reasontext", out string? reasonText);
        fields.TryGetValue("reasonurl", out string? reasonUrl);

        rawValid ??= string.Empty;
        ticket ??= string.Empty;
        reasonCode ??= string.Empty;
        reasonText ??= string.Empty;
        reasonUrl ??= string.Empty;

        return new Net7AuthLoginResponse(
            IsValid: rawValid == "TRUE",
            RawValid: rawValid,
            Ticket: ticket,
            ReasonCode: reasonCode,
            ReasonText: reasonText,
            ReasonUrl: reasonUrl,
            Fields: new ReadOnlyDictionary<string, string>(fields));
    }

    private static string Trim(string value) => value.Trim(TrimChars);
}
