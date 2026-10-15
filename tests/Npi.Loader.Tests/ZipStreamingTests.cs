using System.IO.Compression;
using System.Text;
using Npi.Loader.Datasets;
using Npi.Loader.Db;
using Npi.Loader.Reference;
using Serilog.Core;

namespace Npi.Loader.Tests;

/// <summary>
/// Speed item 9: zip entries are read straight from the zip instead of being extracted or copied out first. Each streamed
/// path must give the same rows (the same bytes) as the extraction it replaces.
/// </summary>
public sealed class ZipStreamingTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _folder = Directory.CreateTempSubdirectory("npi-zipstream-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static StateLicenseSource.DroppedFileDataset Florida(string kind) =>
        StateLicenseSource.Datasets.OfType<StateLicenseSource.DroppedFileDataset>().Single(d => d.State == "FL" && d.Kind == kind);

    private DatasetContext Context() => new(new LoaderOptions
    {
        StateLicenseFilesFolder = Path.Combine(_folder, "state-files"),
        WorkFolder = Path.Combine(_folder, "work"),
    }, new Database("Server=localhost;Database=unused"), new HttpClient(), Logger.None);

    private string DropFloridaZip()
    {
        var target = Path.Combine(_folder, "state-files", "FL", "PROF_ALL.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Fixtures.Path("datasets/fl_prof_all_sample.zip"), target);
        return target;
    }

    // The cleaning as it was before speed item 9: the zip entry extracted to the work folder as clean rows.
    private static async Task<(byte[] Bytes, int Skipped)> ExtractedTheOldWayAsync(string zipPath, string entryName, char separator)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        await using var source = zip.GetEntry(entryName)!.Open();
        using var reader = new StreamReader(source, Encoding.UTF8);
        var target = new MemoryStream();
        var (fields, skipped) = (-1, 0);
        await using (var writer = new StreamWriter(target, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" })
        {
            while (await reader.ReadLineAsync() is { } read)
            {
                var line = read.EndsWith(separator) ? read[..^1] : read;
                var count = line.Count(c => c == separator) + 1;
                if (fields < 0)
                {
                    fields = count;
                }
                else if (count != fields)
                {
                    skipped++;
                    continue;
                }

                await writer.WriteLineAsync(line.Replace('"', '\''));
            }
        }

        return (target.ToArray(), skipped);
    }

    [Fact]
    public async Task A_dropped_zip_streams_the_same_clean_rows_the_old_extraction_wrote()
    {
        var zipPath = DropFloridaZip();
        var (expected, expectedSkipped) = await ExtractedTheOldWayAsync(zipPath, "dbdumps/ldms/PROF_ALL.txt", '|');

        using var zip = ZipFile.OpenRead(zipPath);
        var skipped = -1;
        await using var rows = new StateLicenseSource.DroppedFileRowStream(zip.GetEntry("dbdumps/ldms/PROF_ALL.txt")!.Open(), '|', n => skipped = n);
        var streamed = new MemoryStream();
        await rows.CopyToAsync(streamed, _ct);

        Assert.Equal(expected, streamed.ToArray());
        Assert.Equal((2, 2), (expectedSkipped, skipped)); // the row with a separator inside a value, and the blank line
        Assert.Equal(
            "pro_cde|Profession-Name|Expire-Date|License-Number|Board-Action-Indicator|License-Status-Description|Last-Name|First-Name|Business-Name\n" +
            "1501|Medical Doctor|01/31/2028|ME12345|N|Clear/Active|PATEL|RAJ|\n" +
            "1501|Medical Doctor|01/31/2026|ME99999|N|Delinquent|OTHER|ANN|\n" +
            "1901|Osteopathic Physician|03/31/2027|OS4321|Y|Clear/Active|O'DE|MARIA|\n" +
            "2201|Chiropractic Physician|03/31/2027|CH00777|N|Clear/Active|NÚÑEZ|JOSÉ|NÚÑEZ CHIROPRACTIC\n" +
            "0701|Dentist|02/28/2027|DN 2468|N|Clear/Active|LEE|KIM|SMILE 'CARE' PA\n",
            Encoding.UTF8.GetString(streamed.ToArray()));
    }

    [Fact]
    public void Reading_a_few_bytes_at_a_time_gives_the_same_rows()
    {
        var text = "a|b|c\n1|x\"y|3|\r\nbad|row\n4|5|6|\n";
        using var rows = new StateLicenseSource.DroppedFileRowStream(new MemoryStream(Encoding.UTF8.GetBytes(text)), '|', _ => { });
        var output = new MemoryStream();
        var buffer = new byte[3];
        int n;
        while ((n = rows.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        Assert.Equal("a|b|c\n1|x'y|3\n4|5|6\n", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(0, rows.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public async Task The_filter_keeps_the_same_rows_from_the_stream_as_from_the_extracted_file()
    {
        var zipPath = DropFloridaZip();
        var dataset = Florida("license");
        var context = Context();
        var keys = new LicenseKeys();
        keys.Add("12345", "12345");
        keys.Add("777", "777");
        keys.Add("2468", "2468");

        var (old, _) = await ExtractedTheOldWayAsync(zipPath, "dbdumps/ldms/PROF_ALL.txt", '|');
        var extracted = Path.Combine(_folder, "extracted.txt");
        await File.WriteAllBytesAsync(extracted, old, _ct);
        var fromFile = Path.Combine(_folder, "from_file.csv");
        var fileCounts = await StateLicenseFilter.FilterAsync(extracted, fromFile, dataset, keys, _ct);

        var fromStream = Path.Combine(_folder, "from_stream.csv");
        (long, long) streamCounts;
        await using (var file = await dataset.OpenAsync(context, _ct))
        {
            streamCounts = await StateLicenseFilter.FilterAsync(file.Stream, file.Name, fromStream, dataset, keys, _ct);
        }

        Assert.Equal((5L, 3L), fileCounts);
        Assert.Equal(fileCounts, streamCounts);
        Assert.Equal(await File.ReadAllBytesAsync(fromFile, _ct), await File.ReadAllBytesAsync(fromStream, _ct));
        Assert.False(Directory.Exists(Path.Combine(_folder, "work")), "nothing is written to the work folder");
        Assert.True(File.Exists(zipPath), "the owner's file stays");
    }

    [Fact]
    public async Task Fetching_a_dropped_zip_writes_the_bytes_it_streams()
    {
        var zipPath = DropFloridaZip();
        var (expected, _) = await ExtractedTheOldWayAsync(zipPath, "dbdumps/ldms/PROF_ALL.txt", '|');

        var (path, delete) = await Florida("license").FetchAsync(Context(), _ct);

        Assert.True(delete);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path, _ct));
    }

    [Fact]
    public async Task A_dropped_text_file_streams_too()
    {
        var target = Path.Combine(_folder, "state-files", "FL", "dxe004dd.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "License Number|Respondent Name|Case Number\nME12345|PATEL, RAJ \"DOC\"|2026-001\n", _ct);

        await using var file = await Florida("action").OpenAsync(Context(), _ct);
        using var reader = new StreamReader(file.Stream);

        Assert.Equal("License Number|Respondent Name|Case Number\nME12345|PATEL, RAJ 'DOC'|2026-001\n", await reader.ReadToEndAsync(_ct));
    }

    [Fact]
    public async Task A_zip_without_the_entry_fails_before_reading()
    {
        var target = Path.Combine(_folder, "state-files", "FL", "PROF_ALL.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            zip.CreateEntry("other.txt");
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Florida("license").OpenAsync(Context(), _ct));
        Assert.Contains("dbdumps/ldms/PROF_ALL.txt", error.Message, StringComparison.Ordinal);
        File.Delete(target); // the zip was closed
    }

    [Fact]
    public void The_gazetteer_text_is_parsed_straight_from_its_zip()
    {
        using var zip = File.OpenRead(Fixtures.Path("reference/2026_Gaz_counties_sample.zip"));
        using var text = File.OpenRead(Fixtures.Path("reference/2026_Gaz_counties_sample.txt"));

        var streamed = ReferenceLoader.ReadZippedText(zip, "the counties zip", CensusGeography.ParseGazetteerCounties);

        Assert.NotEmpty(streamed);
        Assert.Equal(CensusGeography.ParseGazetteerCounties(text), streamed);
    }

    [Fact]
    public void A_gazetteer_zip_without_one_text_file_fails()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("a.txt");
            archive.CreateEntry("b.txt");
        }

        zip.Position = 0;
        Assert.Throws<InvalidDataException>(() => ReferenceLoader.ReadZippedText(zip, "the zip", CensusGeography.ParseGazetteerCounties));
    }
}
