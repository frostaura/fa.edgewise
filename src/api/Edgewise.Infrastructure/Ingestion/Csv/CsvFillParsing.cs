using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Edgewise.Domain.Entities;

namespace Edgewise.Infrastructure.Ingestion.Csv;

/// <summary>A parsed CSV: header columns plus rows as column→raw-value maps.</summary>
public sealed record CsvTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<Dictionary<string, string>> Rows);

/// <summary>Maps normalized fill fields to CSV column names (null = not mapped).</summary>
public sealed record CsvFieldMapping
{
    public string? Timestamp { get; init; }
    public string? Symbol { get; init; }
    public string? Side { get; init; }
    public string? Qty { get; init; }
    public string? Price { get; init; }
    public string? Fee { get; init; }
    public string? FeeCurrency { get; init; }

    public Dictionary<string, string?> ToDictionary() => new()
    {
        ["timestamp"] = Timestamp,
        ["symbol"] = Symbol,
        ["side"] = Side,
        ["qty"] = Qty,
        ["price"] = Price,
        ["fee"] = Fee,
        ["feeCurrency"] = FeeCurrency,
    };

    public static CsvFieldMapping FromDictionary(IReadOnlyDictionary<string, string?> map) => new()
    {
        Timestamp = map.GetValueOrDefault("timestamp"),
        Symbol = map.GetValueOrDefault("symbol"),
        Side = map.GetValueOrDefault("side"),
        Qty = map.GetValueOrDefault("qty"),
        Price = map.GetValueOrDefault("price"),
        Fee = map.GetValueOrDefault("fee"),
        FeeCurrency = map.GetValueOrDefault("feeCurrency"),
    };
}

/// <summary>One CSV row normalized into fill fields, with the raw row retained for hashing.</summary>
public sealed record NormalizedFillRow(
    DateTime AtUtc,
    string Symbol,
    bool IsBuy,
    decimal Qty,
    decimal Price,
    decimal Fee,
    string? FeeCurrency,
    string RawRow);

/// <summary>Reads a delimited file into a <see cref="CsvTable"/> using CsvHelper.</summary>
public static class CsvTableReader
{
    public static CsvTable Read(Stream stream, int? maxRows = null)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            BadDataFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim,
            DetectDelimiter = true,
        };

        using var reader = new StreamReader(stream, leaveOpen: true);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read() || !csv.ReadHeader())
        {
            return new CsvTable([], []);
        }

        var columns = (csv.HeaderRecord ?? [])
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .ToList();

        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
        {
            if (maxRows is int limit && rows.Count >= limit)
            {
                break;
            }

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                row[column] = csv.TryGetField<string>(column, out var value) ? value?.Trim() ?? string.Empty : string.Empty;
            }

            if (row.Values.Any(v => v.Length > 0))
            {
                rows.Add(row);
            }
        }

        return new CsvTable(columns, rows);
    }
}

/// <summary>Turns raw CSV rows into normalized fill rows according to a mapping.</summary>
public static partial class CsvFillNormalizer
{
    [GeneratedRegex(@"-?\d[\d,]*(?:\.\d+)?(?:[eE][-+]?\d+)?")]
    private static partial Regex LeadingNumberRegex();

    public static (List<NormalizedFillRow> Rows, List<string> Errors) Normalize(CsvTable table, CsvFieldMapping mapping)
    {
        var rows = new List<NormalizedFillRow>();
        var errors = new List<string>();

        if (mapping.Timestamp is null || mapping.Symbol is null || mapping.Side is null
            || mapping.Qty is null || mapping.Price is null)
        {
            errors.Add("Mapping must include timestamp, symbol, side, qty and price columns.");
            return (rows, errors);
        }

        for (var index = 0; index < table.Rows.Count; index++)
        {
            var row = table.Rows[index];
            var line = index + 2; // 1-based, after the header
            try
            {
                var atRaw = Get(row, mapping.Timestamp);
                if (!TryParseTimestamp(atRaw, out var atUtc))
                {
                    errors.Add($"Row {line}: cannot parse timestamp '{atRaw}'.");
                    continue;
                }

                var symbol = Get(row, mapping.Symbol);
                if (symbol.Length == 0)
                {
                    errors.Add($"Row {line}: symbol is empty.");
                    continue;
                }

                var sideRaw = Get(row, mapping.Side);
                bool isBuy;
                if (LooksLike(sideRaw, "buy", "bought", "b", "bid", "long"))
                {
                    isBuy = true;
                }
                else if (LooksLike(sideRaw, "sell", "sold", "s", "ask", "short"))
                {
                    isBuy = false;
                }
                else
                {
                    errors.Add($"Row {line}: cannot classify side '{sideRaw}'.");
                    continue;
                }

                if (!TryParseNumber(Get(row, mapping.Qty), out var qty) || qty <= 0)
                {
                    errors.Add($"Row {line}: cannot parse qty '{Get(row, mapping.Qty)}'.");
                    continue;
                }

                if (!TryParseNumber(Get(row, mapping.Price), out var price) || price <= 0)
                {
                    errors.Add($"Row {line}: cannot parse price '{Get(row, mapping.Price)}'.");
                    continue;
                }

                decimal fee = 0m;
                string? feeCurrency = null;
                if (mapping.Fee is not null)
                {
                    var feeRaw = Get(row, mapping.Fee);
                    if (feeRaw.Length > 0 && TryParseNumber(feeRaw, out var parsedFee))
                    {
                        fee = Math.Abs(parsedFee);
                        feeCurrency = TrailingLetters(feeRaw);
                    }
                }

                if (mapping.FeeCurrency is not null)
                {
                    var explicitCurrency = Get(row, mapping.FeeCurrency);
                    if (explicitCurrency.Length > 0)
                    {
                        feeCurrency = explicitCurrency.ToUpperInvariant();
                    }
                }

                var rawRow = string.Join("|", table.Columns.Select(c => row.GetValueOrDefault(c, string.Empty)));
                rows.Add(new NormalizedFillRow(atUtc, symbol, isBuy, qty, price, fee, feeCurrency, rawRow));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                errors.Add($"Row {line}: {ex.Message}");
            }
        }

        return (rows, errors);
    }

    private static string Get(Dictionary<string, string> row, string column) =>
        row.GetValueOrDefault(column, string.Empty).Trim();

    private static bool LooksLike(string value, params string[] candidates)
    {
        var lower = value.Trim().ToLowerInvariant();
        return candidates.Any(c => lower == c || lower.StartsWith(c, StringComparison.Ordinal));
    }

    public static bool TryParseTimestamp(string raw, out DateTime utc)
    {
        utc = default;
        if (raw.Length == 0)
        {
            return false;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        // Unix epoch seconds or milliseconds.
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            var offset = epoch > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
            utc = offset.UtcDateTime;
            return true;
        }

        return false;
    }

    /// <summary>Parses the leading number of a value like "0.00512BTC" or "1,234.56 USDT".</summary>
    public static bool TryParseNumber(string raw, out decimal value)
    {
        value = 0m;
        var match = LeadingNumberRegex().Match(raw);
        if (!match.Success)
        {
            return false;
        }

        return decimal.TryParse(match.Value.Replace(",", string.Empty),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Extracts a trailing currency code like the "BTC" in "0.0000012BTC".</summary>
    public static string? TrailingLetters(string raw)
    {
        var trimmed = raw.Trim();
        var i = trimmed.Length;
        while (i > 0 && char.IsAsciiLetter(trimmed[i - 1]))
        {
            i--;
        }

        var letters = trimmed[i..];
        return letters.Length is >= 2 and <= 6 ? letters.ToUpperInvariant() : null;
    }
}

/// <summary>Column-mapping suggestions per venue export format.</summary>
public static class VenueMappingSuggestions
{
    /// <summary>Detects the venue from the header shape and suggests a mapping.</summary>
    public static (Venue Venue, CsvFieldMapping Mapping) Suggest(IReadOnlyList<string> columns, Venue? venueHint = null)
    {
        var venue = venueHint ?? Detect(columns);
        var mapping = venue switch
        {
            Venue.Binance => Pick(columns, new CsvFieldMapping
            {
                Timestamp = "Date(UTC)",
                Symbol = "Pair",
                Side = "Side",
                Price = "Price",
                Qty = "Executed",
                Fee = "Fee",
            }),
            Venue.Coinbase => Pick(columns, new CsvFieldMapping
            {
                Timestamp = "created at",
                Symbol = "product",
                Side = "side",
                Price = "price",
                Qty = "size",
                Fee = "fee",
                FeeCurrency = "price/fee/total unit",
            }),
            _ => Fuzzy(columns),
        };

        // Any preset column that is missing falls back to a fuzzy guess.
        if (mapping.Timestamp is null || mapping.Symbol is null || mapping.Side is null
            || mapping.Qty is null || mapping.Price is null)
        {
            var fuzzy = Fuzzy(columns);
            mapping = new CsvFieldMapping
            {
                Timestamp = mapping.Timestamp ?? fuzzy.Timestamp,
                Symbol = mapping.Symbol ?? fuzzy.Symbol,
                Side = mapping.Side ?? fuzzy.Side,
                Qty = mapping.Qty ?? fuzzy.Qty,
                Price = mapping.Price ?? fuzzy.Price,
                Fee = mapping.Fee ?? fuzzy.Fee,
                FeeCurrency = mapping.FeeCurrency ?? fuzzy.FeeCurrency,
            };
        }

        return (venue, mapping);
    }

    private static Venue Detect(IReadOnlyList<string> columns)
    {
        bool Has(string name) => columns.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

        if (Has("Date(UTC)") && Has("Pair"))
        {
            return Venue.Binance;
        }

        if (Has("product") && Has("created at"))
        {
            return Venue.Coinbase;
        }

        if (columns.Any(c => c.Contains("brokerage", StringComparison.OrdinalIgnoreCase))
            || columns.Any(c => c.Contains("contract", StringComparison.OrdinalIgnoreCase) && Has("Action")))
        {
            return Venue.EasyEquities;
        }

        return Venue.Generic;
    }

    /// <summary>Keeps only preset columns that exist in the file (case-insensitive).</summary>
    private static CsvFieldMapping Pick(IReadOnlyList<string> columns, CsvFieldMapping preset)
    {
        string? Find(string? name) => name is null
            ? null
            : columns.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

        return new CsvFieldMapping
        {
            Timestamp = Find(preset.Timestamp),
            Symbol = Find(preset.Symbol),
            Side = Find(preset.Side),
            Qty = Find(preset.Qty),
            Price = Find(preset.Price),
            Fee = Find(preset.Fee),
            FeeCurrency = Find(preset.FeeCurrency),
        };
    }

    private static CsvFieldMapping Fuzzy(IReadOnlyList<string> columns)
    {
        string? Match(params string[] needles) =>
            columns.FirstOrDefault(c => needles.Any(n => string.Equals(c, n, StringComparison.OrdinalIgnoreCase)))
            ?? columns.FirstOrDefault(c => needles.Any(n => c.Contains(n, StringComparison.OrdinalIgnoreCase)));

        return new CsvFieldMapping
        {
            Timestamp = Match("timestamp", "date(utc)", "created at", "trade date", "date", "time"),
            Symbol = Match("symbol", "pair", "product", "instrument", "ticker", "contract", "share", "market"),
            Side = Match("side", "action", "buy/sell", "direction", "type"),
            Qty = Match("qty", "quantity", "executed", "size", "units", "amount"),
            Price = Match("price", "unit price", "rate"),
            Fee = Match("fee", "fees", "commission", "brokerage", "total costs"),
            FeeCurrency = Match("fee currency", "fee coin", "feecoin"),
        };
    }
}
