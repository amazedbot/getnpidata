using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;

namespace Npi.Loader.Datasets;

/// <summary>
/// The license numbers NPPES providers list for one state, in the two forms <see cref="StateLicenseSource"/> matches on:
/// all the digits without leading zeros, and the last run of digits without leading zeros (the same rules as
/// state_license_raw's generated license_key and license_key_last, and the matching SQL's keys for provider_taxonomy).
/// </summary>
public sealed partial class LicenseKeys
{
    private readonly HashSet<string> _all = new(StringComparer.Ordinal);
    private readonly HashSet<string> _last = new(StringComparer.Ordinal);

    public int Count => _all.Count;

    /// <summary>Adds a provider's keys (null keys are skipped).</summary>
    public void Add(string? all, string? last)
    {
        if (all is not null)
        {
            _all.Add(all);
        }

        if (last is not null)
        {
            _last.Add(last);
        }
    }

    /// <summary>True when a state's license number could match a provider's: its all-digits key or its last-digits key is known.</summary>
    public bool CouldMatch(string? licenseNumber) =>
        All(licenseNumber) is { } all && _all.Contains(all) || Last(licenseNumber) is { } last && _last.Contains(last);

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Digits();

    /// <summary>Every digit, leading zeros dropped; null when none is left.</summary>
    public static string? All(string? number)
    {
        if (string.IsNullOrEmpty(number))
        {
            return null;
        }

        var digits = new string(number.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
        return digits.Length > 0 ? digits : null;
    }

    /// <summary>The last run of digits, leading zeros dropped; null when there is none.</summary>
    public static string? Last(string? number)
    {
        if (string.IsNullOrEmpty(number) || Digits().Matches(number) is not { Count: > 0 } runs)
        {
            return null;
        }

        var last = runs[^1].Value.TrimStart('0');
        return last.Length > 0 ? last : null;
    }
}

/// <summary>
/// Keeps only the rows of a state license file whose license number could match an NPPES provider's license in that state
/// (<see cref="LicenseKeys"/>), before the file is loaded: most rows of a state's file are other professions (barbers,
/// accountants, engineers) or people without an NPI. The file is read and written as Latin-1, which round-trips every
/// byte, so UTF-8 and Windows-1252 files come out unchanged; the output has the header first and every field quoted.
/// </summary>
public static class StateLicenseFilter
{
    /// <returns>The rows read and the rows kept.</returns>
    public static async Task<(long Read, long Kept)> FilterAsync(string source, string target, StateLicenseSource.StateDataset dataset, LicenseKeys keys,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(source);
        return await FilterAsync(stream, Path.GetFileName(source), target, dataset, keys, ct);
    }

    /// <summary>The same, reading the rows from a stream (a dropped file's zip entry, never extracted); <paramref name="name"/> is for messages.</summary>
    /// <returns>The rows read and the rows kept.</returns>
    public static async Task<(long Read, long Kept)> FilterAsync(Stream source, string name, string target, StateLicenseSource.StateDataset dataset,
        LicenseKeys keys, CancellationToken ct)
    {
        var licenseHeader = dataset.Columns.First(c => c.Column == "license_number").Header;
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = dataset.Delimiter.ToString(),
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
            DetectColumnCountChanges = false,
        };
        using var reader = new StreamReader(source, Encoding.Latin1);
        using var csv = new CsvReader(reader, config);
        await using var writer = new StreamWriter(target, false, Encoding.Latin1) { NewLine = "\n" };
        await using var output = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = dataset.Delimiter.ToString(),
            ShouldQuote = _ => true,
            NewLine = "\n",
        });

        // The header: the first record, or the first whose first field is HeaderStartsWith (a letterhead before it).
        string[]? header = null;
        while (header is null && await csv.ReadAsync())
        {
            var record = csv.Parser.Record ?? [];
            var first = record.Length > 0 ? Clean(record[0]) : "";
            if (dataset.HeaderStartsWith is null || string.Equals(first, dataset.HeaderStartsWith, StringComparison.Ordinal))
            {
                header = record;
            }
        }

        var column = header is null ? -1 : Array.FindIndex(header, h => string.Equals(Clean(h), licenseHeader, StringComparison.OrdinalIgnoreCase));
        if (column < 0)
        {
            throw new InvalidDataException($"{name} has no column '{licenseHeader}'; the source changed its format.");
        }

        foreach (var field in header!)
        {
            output.WriteField(Clean(field));
        }

        await output.NextRecordAsync();
        var (read, kept) = (0L, 0L);
        while (await csv.ReadAsync())
        {
            var record = csv.Parser.Record ?? [];
            if (record.Length == 1 && record[0].Length == 0)
            {
                continue; // a blank line
            }

            read++;
            if (column < record.Length && keys.CouldMatch(record[column]))
            {
                kept++;
                foreach (var field in record)
                {
                    output.WriteField(field);
                }

                await output.NextRecordAsync();
            }

            if (read % 500_000 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
        }

        return (read, kept);
    }

    // A header field without a byte order mark (read as Latin-1, a UTF-8 BOM is "ï»¿") or surrounding spaces.
    private static string Clean(string field) => field.Replace("ï»¿", "", StringComparison.Ordinal).TrimStart('﻿').Trim();
}
