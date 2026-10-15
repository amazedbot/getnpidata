using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npi.Core;
using Npi.Core.Search;
using Npi.Loader.Reference;

namespace Npi.Loader.Datasets;

/// <summary>A health system in AHRQ's Compendium systems file.</summary>
public sealed record HealthSystemRow(string SystemId, string Name, string? City, string? State, int DataYear, int? Physicians, int? PrimaryCarePhysicians,
    int? NursePractitioners, int? PhysicianAssistants, int? GroupPractices, int? Hospitals, int? AcuteHospitals, int? NursingHomes, int? HomeHealthAgencies,
    int? Beds, int? Discharges, bool? MajorTeaching, bool? OffersInsurance, bool? MedicareAdvantage);

/// <summary>A facility (by CCN) in one of the Compendium's linkage files, with the system it belongs to.</summary>
public sealed record HealthSystemFacilityRow(string Ccn, string SystemId, string Kind, string? Name, string? City, string? State);

/// <summary>
/// AHRQ's Compendium of U.S. Health Systems (https://www.ahrq.gov/chsp/data-resources/compendium.html): a systems file
/// (chsp-compendium-&lt;year&gt;.csv, one row per system, keyed by health_sys_id) and linkage files that place facilities in
/// systems by CMS Certification Number (hospitals; for 2023 also nursing homes and home health agencies). The files are told
/// apart by their columns, not their names: a systems file has health_sys_name and no ccn; a linkage file has ccn and health_sys_id.
/// </summary>
public static partial class AhrqCompendium
{
    public enum FileKind
    {
        Unknown,
        Systems,
        Linkage,
    }

    public static FileKind Classify(NamedCsv csv) =>
        csv.Has("ccn") && csv.Has("health_sys_id") ? FileKind.Linkage
        : csv.Has("health_sys_id") && csv.Has("health_sys_name") ? FileKind.Systems
        : FileKind.Unknown;

    /// <summary>The data year in a file name as AHRQ names them ("chsp-compendium-2023.csv", "chsp-hospital-linkage-2023.csv").</summary>
    public static int? YearOf(string fileName) =>
        YearPattern().Match(Path.GetFileNameWithoutExtension(fileName)) is { Success: true } m ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;

    public static IReadOnlyList<HealthSystemRow> ReadSystems(NamedCsv csv, int year, string file = "systems file")
    {
        csv.Require(file, "health_sys_id", "health_sys_name");
        var rows = new Dictionary<string, HealthSystemRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in csv.Rows)
        {
            if (r["health_sys_id"] is not { } id || r["health_sys_name"] is not { } name || id.Length > 20)
            {
                continue;
            }

            rows.TryAdd(id, new HealthSystemRow(id.ToUpperInvariant(), Clip(name, 200)!, Clip(r["health_sys_city"], 100), State(r["health_sys_state"]), year,
                r.Whole("total_mds"), r.Whole("prim_care_mds"), r.Whole("total_nps"), r.Whole("total_pas"), r.Whole("grp_cnt"), r.Whole("hosp_cnt"), r.Whole("acutehosp_cnt"),
                r.Whole("nh_cnt"), r.Whole("hhco_cnt"), r.Whole("sys_beds"), r.Whole("sys_dsch"), r.Flag("sys_incl_majteachhosp"), r.Flag("sys_anyins_product"),
                r.Flag("sys_mcare_adv")));
        }

        return [.. rows.Values];
    }

    /// <summary>
    /// The facilities of a linkage file that belong to a system (the hospital file lists hospitals outside any system too, with no
    /// health_sys_id). The facility's name, city and state come from the first column the file has of each kind.
    /// </summary>
    public static IReadOnlyList<HealthSystemFacilityRow> ReadLinkage(NamedCsv csv, string file = "linkage file")
    {
        csv.Require(file, "ccn", "health_sys_id");
        // The 2023 files' columns: hospital_*, nursing_home_*, home_health_care_org_* (the home health file also lists hospices and
        // home dialysis, in home_health_care_org_type).
        var name = csv.First("hospital_name", "nursing_home_name", "home_health_care_org_name", "hosp_name", "nh_name", "hhco_name");
        var city = csv.First("hospital_city", "nursing_home_city", "home_health_care_org_city", "hosp_city", "nh_city", "hhco_city");
        var state = csv.First("hospital_state", "nursing_home_state", "home_health_care_org_state", "hosp_state", "nh_state", "hhco_state");
        var fileKind = name switch
        {
            "hospital_name" or "hosp_name" => FacilityTypes.Hospital,
            "nursing_home_name" or "nh_name" => FacilityTypes.NursingHome,
            _ => null,
        };
        var rows = new List<HealthSystemFacilityRow>();
        foreach (var r in csv.Rows)
        {
            if (Ccn(r["ccn"]) is not { } ccn || r["health_sys_id"] is not { Length: <= 20 } id)
            {
                continue;
            }

            var kind = r["home_health_care_org_type"]?.ToUpperInvariant() switch
            {
                "HOME HEALTH" => FacilityTypes.HomeHealth,
                "HOSPICE" => FacilityTypes.Hospice,
                "HOME DIALYSIS" => FacilityTypes.Dialysis,
                _ => fileKind ?? FacilityTypes.FromCcn(ccn).Type,
            };
            rows.Add(new HealthSystemFacilityRow(ccn, id.ToUpperInvariant(), kind, Clip(r[name], 200), Clip(r[city], 100), State(r[state])));
        }

        return rows;
    }

    /// <summary>A CMS Certification Number: 6 or 10 letters and digits, upper-cased; 5 digits had a leading zero dropped (a spreadsheet's doing).</summary>
    public static string? Ccn(string? value)
    {
        var id = value?.Trim().ToUpperInvariant() ?? "";
        if (id.Length == 5 && id.All(char.IsAsciiDigit))
        {
            id = "0" + id;
        }

        return id.Length is 6 or 10 && id.All(char.IsAsciiLetterOrDigit) ? id : null;
    }

    internal static string? State(string? value) => value?.Trim().ToUpperInvariant() is { Length: 2 } s && s.All(char.IsAsciiLetter) ? s : null;

    internal static string? Clip(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];

    [GeneratedRegex(@"(?<![0-9])20[0-9]{2}(?![0-9])")]
    private static partial Regex YearPattern();
}

/// <summary>
/// AHRQ's Compendium of U.S. Health Systems → <c>health_system</c>, <c>health_system_facility</c> (docs/PRODUCT_PLAN.md step 3.6
/// part 2). ahrq.gov answers programs with a browser check, so the owner downloads the CSV files (the systems file and the
/// linkage files of the same year) into <see cref="LoaderOptions.AhrqCompendiumFolder"/>; the newest year there with a systems
/// file is loaded. Without files the source is skipped (version "no files"), so the run doesn't fail. The version is the year
/// and a hash of the files' names, sizes and times, so a new download reloads.
/// </summary>
public sealed class HealthSystemSource : DatasetSource
{
    public const string NoFiles = "no files";

    public override string Name => "ahrq_compendium";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(1);

    /// <summary>The newest year's systems file and the linkage files of that year (or without a year in their name).</summary>
    public static (int Year, string Systems, IReadOnlyList<string> Linkage)? Choose(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(folder, "*.csv").Order(StringComparer.OrdinalIgnoreCase).ToList();
        var systems = new List<(int Year, string Path)>();
        var linkage = new List<(int? Year, string Path)>();
        foreach (var path in files)
        {
            NamedCsv head;
            using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
            {
                // Only the header decides; the files are small, but reading the first line is enough.
                head = NamedCsv.Read(new StringReader(reader.ReadLine() ?? ""));
            }

            switch (AhrqCompendium.Classify(head))
            {
                case AhrqCompendium.FileKind.Systems when AhrqCompendium.YearOf(path) is { } year:
                    systems.Add((year, path));
                    break;
                case AhrqCompendium.FileKind.Linkage:
                    linkage.Add((AhrqCompendium.YearOf(path), path));
                    break;
            }
        }

        if (systems.Count == 0)
        {
            return null;
        }

        var newest = systems.OrderByDescending(s => s.Year).ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase).First();
        return (newest.Year, newest.Path, [.. linkage.Where(l => l.Year is null || l.Year == newest.Year).Select(l => l.Path)]);
    }

    public override Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var page = new Uri(context.Options.AhrqCompendiumPageUrl);
        if (Choose(context.Options.ResolvedAhrqCompendiumFolder) is not { } chosen)
        {
            return Task.FromResult(new DatasetRelease(NoFiles, page));
        }

        var stamp = string.Join("\n", new[] { chosen.Systems }.Concat(chosen.Linkage).Select(p =>
        {
            var f = new FileInfo(p);
            return $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc:O}";
        }));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..12];
        return Task.FromResult(new DatasetRelease($"{chosen.Year} ({1 + chosen.Linkage.Count} files) {hash}", page, $"{chosen.Year}-12-31"));
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var folder = context.Options.ResolvedAhrqCompendiumFolder;
        if (Choose(folder) is not { } chosen)
        {
            context.Log.Warning("AHRQ Compendium skipped: no systems file (chsp-compendium-<year>.csv) in {Folder}; download the CSV files from {Page}",
                folder, context.Options.AhrqCompendiumPageUrl);
            return 0;
        }

        if (chosen.Linkage.Count == 0)
        {
            throw new InvalidDataException($"AHRQ Compendium {chosen.Year}: no linkage file (with ccn and health_sys_id columns) in {folder}; download the hospital linkage CSV too.");
        }

        var systems = AhrqCompendium.ReadSystems(NamedCsv.Read(chosen.Systems), chosen.Year, Path.GetFileName(chosen.Systems));
        var known = systems.Select(s => s.SystemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var facilities = new Dictionary<(string, string), HealthSystemFacilityRow>();
        var unknown = 0;
        foreach (var path in chosen.Linkage)
        {
            var rows = AhrqCompendium.ReadLinkage(NamedCsv.Read(path), Path.GetFileName(path));
            foreach (var row in rows)
            {
                if (!known.Contains(row.SystemId))
                {
                    unknown++;
                    continue;
                }

                facilities.TryAdd((row.Ccn, row.SystemId), row);
            }

            context.Log.Information("AHRQ Compendium {Year}: {File} {Rows:N0} facilities in a system", chosen.Year, Path.GetFileName(path), rows.Count);
        }

        if (unknown > 0)
        {
            context.Log.Warning("AHRQ Compendium {Year}: {Rows:N0} linkage rows name a system the systems file doesn't list; left out", chosen.Year, unknown);
        }

        await using var connection = await context.Database.OpenAsync(ct);
        var counts = await TableSwap.ReplaceAsync(connection, ["health_system", "health_system_facility"], context.Options.MinRowRatio, async () =>
        {
            await ReferenceTables.CopyToStagingAsync(connection, "health_system", ReferenceTables.ToDataTable(systems,
                ("system_id", typeof(string), s => s.SystemId), ("name", typeof(string), s => s.Name), ("city", typeof(string), s => s.City),
                ("state", typeof(string), s => s.State), ("data_year", typeof(short), s => (short)s.DataYear), ("physicians", typeof(int), s => s.Physicians),
                ("primary_care_physicians", typeof(int), s => s.PrimaryCarePhysicians), ("nurse_practitioners", typeof(int), s => s.NursePractitioners),
                ("physician_assistants", typeof(int), s => s.PhysicianAssistants), ("group_practices", typeof(int), s => s.GroupPractices),
                ("hospitals", typeof(int), s => s.Hospitals), ("acute_hospitals", typeof(int), s => s.AcuteHospitals), ("nursing_homes", typeof(int), s => s.NursingHomes),
                ("home_health_agencies", typeof(int), s => s.HomeHealthAgencies), ("beds", typeof(int), s => s.Beds), ("discharges", typeof(int), s => s.Discharges),
                ("major_teaching", typeof(sbyte), s => Flag(s.MajorTeaching)), ("offers_insurance", typeof(sbyte), s => Flag(s.OffersInsurance)),
                ("medicare_advantage", typeof(sbyte), s => Flag(s.MedicareAdvantage))), ct);
            await ReferenceTables.CopyToStagingAsync(connection, "health_system_facility", ReferenceTables.ToDataTable(facilities.Values,
                ("ccn", typeof(string), f => f.Ccn), ("system_id", typeof(string), f => f.SystemId), ("kind", typeof(string), f => f.Kind),
                ("name", typeof(string), f => f.Name), ("city", typeof(string), f => f.City), ("state", typeof(string), f => f.State)), ct);
        }, ct);
        context.Log.Information("AHRQ Compendium {Year}: {Systems:N0} systems, {Facilities:N0} facilities", chosen.Year, counts["health_system"], counts["health_system_facility"]);
        return counts["health_system"] + counts["health_system_facility"];
    }

    private static sbyte? Flag(bool? value) => value is null ? null : (sbyte)(value.Value ? 1 : 0);
}

/// <summary>An ACO in CMS's "Accountable Care Organizations" file.</summary>
public sealed record AcoRow(string AcoId, string Name, string? ServiceArea, int? AgreementPeriod, DateTime? InitialStartDate, DateTime? CurrentStartDate,
    string? Track, bool? HighRevenue, string? Address, string? Website, double? Latitude, double? Longitude);

/// <summary>The Medicare Shared Savings Program files: ACOs, and their participants and SNF affiliates by legal business name.</summary>
public static class AcoFiles
{
    public static IReadOnlyList<AcoRow> ReadOrganizations(NamedCsv csv, string file = "ACO file")
    {
        csv.Require(file, "aco_id", "aco_name");
        var rows = new Dictionary<string, AcoRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in csv.Rows)
        {
            if (Id(r["aco_id"]) is not { } id || r["aco_name"] is not { } name)
            {
                continue;
            }

            var lat = r.Number("lat");
            var lon = r.Number("long") ?? r.Number("lon");
            rows.TryAdd(id, new AcoRow(id, AhrqCompendium.Clip(name, 200)!, AhrqCompendium.Clip(r["aco_service_area"], 200),
                r.Whole("agreement_period_num") is { } p and >= 0 and <= 100 ? p : null, r.Date("initial_start_date"), r.Date("current_start_date"),
                Track(r["basic_track"], r["basic_track_level"], r["enhanced_track"]), r.Flag("high_revenue_aco"), AhrqCompendium.Clip(r["aco_address"], 255),
                AhrqCompendium.Clip(r["aco_public_reporting_website"], 255), lat is >= -90 and <= 90 ? lat : null, lon is >= -180 and <= 180 ? lon : null));
        }

        return [.. rows.Values];
    }

    /// <summary>(ACO ID, name) pairs of a participant or SNF affiliate file (<paramref name="nameHeader"/>: par_lbn, aff_lbn), each once.</summary>
    public static IReadOnlyList<(string AcoId, string Name)> ReadNames(NamedCsv csv, string nameHeader, string file = "ACO file")
    {
        csv.Require(file, "aco_id", nameHeader);
        var seen = new HashSet<(string, string)>();
        var rows = new List<(string, string)>();
        foreach (var r in csv.Rows)
        {
            if (Id(r["aco_id"]) is { } id && AhrqCompendium.Clip(r[nameHeader], 200) is { } name
                && seen.Add((id, name.ToUpperInvariant())))
            {
                rows.Add((id, name));
            }
        }

        return rows;
    }

    /// <summary>"BASIC level E", "ENHANCED", or null.</summary>
    public static string? Track(string? basic, string? level, string? enhanced) =>
        enhanced == "1" ? "ENHANCED"
        : basic == "1" ? (level?.Trim() is { Length: 1 } l && char.IsAsciiLetter(l[0]) ? $"BASIC level {char.ToUpperInvariant(l[0])}" : "BASIC")
        : null;

    /// <summary>An ACO ID: a letter and four digits (A1234), at most 10 characters.</summary>
    public static string? Id(string? value) =>
        value?.Trim().ToUpperInvariant() is { Length: >= 2 and <= 10 } id && char.IsAsciiLetter(id[0]) && id.All(char.IsAsciiLetterOrDigit) ? id : null;
}

/// <summary>
/// The Medicare Shared Savings Program's accountable care organizations (data.cms.gov catalog: "Accountable Care Organizations",
/// "Accountable Care Organization Participants", "Accountable Care Organization Skilled Nursing Facility Affiliates", newest
/// performance year) → <c>aco</c>, <c>aco_participant</c>, <c>aco_snf_affiliate</c> (docs/PRODUCT_PLAN.md step 3.6 part 2). The
/// participant and affiliate files name each by legal business name only (no TIN, NPI or CCN), so nothing links them to our
/// providers, groups or facilities: matches by name alone are never made. The files' contact people are not loaded.
/// </summary>
public sealed class AcoSource : DatasetSource
{
    public const string OrganizationsTitle = "Accountable Care Organizations";
    public const string ParticipantsTitle = "Accountable Care Organization Participants";
    public const string SnfTitle = "Accountable Care Organization Skilled Nursing Facility Affiliates";

    public override string Name => "cms_aco";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    private static async Task<(DatasetRelease Acos, DatasetRelease Participants, DatasetRelease Snf)> FindAllAsync(DatasetContext context, CancellationToken ct)
    {
        var catalog = await context.CmsCatalogAsync(ct);
        DatasetRelease Find(string title) => catalog.FindLatest(title) ?? throw new InvalidDataException($"data.cms.gov lists no \"{title}\" CSV.");
        return (Find(OrganizationsTitle), Find(ParticipantsTitle), Find(SnfTitle));
    }

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var (acos, participants, snf) = await FindAllAsync(context, ct);
        return new DatasetRelease($"{acos.Version} | {participants.Version} | {snf.Version}", acos.Url, acos.PeriodEnd);
    }

    public override async Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct)
    {
        var (acosRelease, participantsRelease, snfRelease) = await FindAllAsync(context, ct);
        var year = acosRelease.DataYear ?? DateTime.UtcNow.Year;
        var paths = new List<string>();
        try
        {
            paths.Add(await context.DownloadAsync(acosRelease, "cms_aco_organizations.csv", ct));
            paths.Add(await context.DownloadAsync(participantsRelease, "cms_aco_participants.csv", ct));
            paths.Add(await context.DownloadAsync(snfRelease, "cms_aco_snf_affiliates.csv", ct));
            var acos = AcoFiles.ReadOrganizations(NamedCsv.Read(paths[0]), OrganizationsTitle);
            var known = acos.Select(a => a.AcoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var participants = AcoFiles.ReadNames(NamedCsv.Read(paths[1]), "par_lbn", ParticipantsTitle).Where(p => known.Contains(p.AcoId)).ToList();
            var snf = AcoFiles.ReadNames(NamedCsv.Read(paths[2]), "aff_lbn", SnfTitle).Where(p => known.Contains(p.AcoId)).ToList();

            await using var connection = await context.Database.OpenAsync(ct);
            var counts = await TableSwap.ReplaceAsync(connection, ["aco", "aco_participant", "aco_snf_affiliate"], context.Options.MinRowRatio, async () =>
            {
                await ReferenceTables.CopyToStagingAsync(connection, "aco", ReferenceTables.ToDataTable(acos,
                    ("aco_id", typeof(string), a => a.AcoId), ("name", typeof(string), a => a.Name), ("program_year", typeof(short), _ => (short)year),
                    ("service_area", typeof(string), a => a.ServiceArea), ("agreement_period", typeof(sbyte), a => a.AgreementPeriod is { } p ? (sbyte)p : null),
                    ("initial_start_date", typeof(DateTime), a => a.InitialStartDate), ("current_start_date", typeof(DateTime), a => a.CurrentStartDate),
                    ("track", typeof(string), a => a.Track), ("high_revenue", typeof(sbyte), a => a.HighRevenue is { } h ? (sbyte)(h ? 1 : 0) : null),
                    ("address", typeof(string), a => a.Address), ("website", typeof(string), a => a.Website),
                    ("lat", typeof(double), a => a.Latitude), ("lon", typeof(double), a => a.Longitude)), ct);
                await ReferenceTables.CopyToStagingAsync(connection, "aco_participant", ReferenceTables.ToDataTable(participants,
                    ("aco_id", typeof(string), p => p.AcoId), ("name", typeof(string), p => p.Name)), ct);
                await ReferenceTables.CopyToStagingAsync(connection, "aco_snf_affiliate", ReferenceTables.ToDataTable(snf,
                    ("aco_id", typeof(string), p => p.AcoId), ("name", typeof(string), p => p.Name)), ct);
            }, ct);
            context.Log.Information("ACOs {Year}: {Acos:N0} ACOs, {Participants:N0} participants, {Snf:N0} SNF affiliates", year, counts["aco"],
                counts["aco_participant"], counts["aco_snf_affiliate"]);
            return counts["aco"] + counts["aco_participant"] + counts["aco_snf_affiliate"];
        }
        finally
        {
            foreach (var path in paths)
            {
                FileDownloader.Delete(path);
            }
        }
    }
}

/// <summary>A site in HRSA's Health Center Service Delivery and Look-Alike Sites file.</summary>
public sealed record HealthCenterSiteRow(string SiteId, string Name, string? Address, string? City, string? State, string? Zip5, string? Phone, string? Website,
    double? HoursPerWeek, string? Setting, string? LocationType, string? SiteType, bool LookAlike, string? GrantNumber, string? HealthCenter,
    string? HealthCenterCity, string? HealthCenterState, string? Ccn, string? Npi, DateTime? AddedDate, string? CountyFips, double? Latitude, double? Longitude);

/// <summary>HRSA's health center sites file, read by its column names.</summary>
public static class HealthCenterSiteFile
{
    public static IReadOnlyList<HealthCenterSiteRow> Read(NamedCsv csv, string file = "health center sites file")
    {
        csv.Require(file, "BPHC Assigned Number", "Site Name", "Health Center Type", "Health Center Type Description", "Site Status Description",
            "FQHC Site Medicare Billing Number", "FQHC Site NPI Number", "Health Center Number", "Health Center Name");
        var rows = new Dictionary<string, HealthCenterSiteRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in csv.Rows)
        {
            // Active service delivery sites only: an administrative office sees no patients.
            if (r["BPHC Assigned Number"] is not { Length: <= 20 } id || r["Site Name"] is not { } name
                || !string.Equals(r["Site Status Description"], "Active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(r["Health Center Type Description"], "Administrative", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lat = r.Number("Geocoding Artifact Address Primary Y Coordinate");
            var lon = r.Number("Geocoding Artifact Address Primary X Coordinate");
            var zip = r["Site Postal Code"] is { Length: >= 5 } z && z[..5].All(char.IsAsciiDigit) ? z[..5] : null;
            var fips = r["State and County Federal Information Processing Standard Code"] is { Length: 5 } f && f.All(char.IsAsciiDigit) ? f : null;
            var npi = r["FQHC Site NPI Number"] is { } n && InputFormats.IsNpi(n) ? n : null;
            var hours = r.Number("Operating Hours per Week");
            rows.TryAdd(id, new HealthCenterSiteRow(id.ToUpperInvariant(), AhrqCompendium.Clip(name, 200)!, AhrqCompendium.Clip(r["Site Address"], 200),
                AhrqCompendium.Clip(r["Site City"], 100), AhrqCompendium.State(r["Site State Abbreviation"]), zip, Phone(r["Site Telephone Number"]),
                AhrqCompendium.Clip(r["Site Web Address"], 255), hours is > 0 and <= 168 ? hours : null,
                Known(r["Health Center Service Delivery Site Location Setting Description"]), AhrqCompendium.Clip(r["Health Center Location Type Description"], 30),
                AhrqCompendium.Clip(r["Health Center Type Description"], 60),
                r["Health Center Type"]?.Contains("Look-Alike", StringComparison.OrdinalIgnoreCase) == true,
                AhrqCompendium.Clip(r["Health Center Number"], 20), AhrqCompendium.Clip(r["Health Center Name"], 200), AhrqCompendium.Clip(r["Health Center Organization City"], 100),
                AhrqCompendium.State(r["Health Center Organization State"]), AhrqCompendium.Ccn(r["FQHC Site Medicare Billing Number"]) is { Length: 6 } ccn ? ccn : null, npi,
                r.Date("Site Added to Scope this Date"), fips, lat is >= -90 and <= 90 && lat != 0 ? lat : null, lon is >= -180 and <= 180 && lon != 0 ? lon : null));
        }

        return [.. rows.Values];
    }

    /// <summary>The digits of a 10-digit US phone number ("803-433-4124" → "8034334124"); anything else null.</summary>
    public static string? Phone(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '1')
        {
            digits = digits[1..];
        }

        return digits.Length == 10 ? digits : null;
    }

    // HRSA's "Unknown" and "All Other Clinic Types" say nothing about the setting.
    private static string? Known(string? setting) =>
        setting is null || setting.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || setting.Equals("All Other Clinic Types", StringComparison.OrdinalIgnoreCase)
            ? null
            : AhrqCompendium.Clip(setting, 60);
}

/// <summary>
/// HRSA's Health Center Service Delivery and Look-Alike Sites (data.hrsa.gov data downloads, rebuilt daily, checked weekly) →
/// <c>hrsa_health_center_site</c> (docs/PRODUCT_PLAN.md step 3.6 part 2): every active site of a federally funded health center
/// or look-alike, with its Medicare billing number (CCN) and NPI where HRSA lists them. The facility list matches a site to a
/// certified facility by those numbers (DirectoryBuilder). Version = the file's Last-Modified and size.
/// </summary>
public sealed class HealthCenterSiteSource : DatasetSource
{
    public override string Name => "hrsa_health_center_sites";

    public override TimeSpan CheckInterval => TimeSpan.FromDays(7);

    public override async Task<DatasetRelease> FindLatestAsync(DatasetContext context, CancellationToken ct)
    {
        var url = new Uri(context.Options.HrsaHealthCenterSitesUrl);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await context.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var modified = response.Content.Headers.LastModified?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                       ?? $"checked {DateTime.UtcNow:yyyy-MM-dd}";
        return new DatasetRelease($"{modified} {response.Content.Headers.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "?"} bytes", url);
    }

    public override Task<long> LoadAsync(DatasetContext context, DatasetRelease release, CancellationToken ct) =>
        WithDownloadAsync(context, release, "hrsa_health_center_sites.csv", async (connection, path) =>
        {
            var sites = HealthCenterSiteFile.Read(NamedCsv.Read(path), Path.GetFileName(context.Options.HrsaHealthCenterSitesUrl));
            var counts = await TableSwap.ReplaceAsync(connection, ["hrsa_health_center_site"], context.Options.MinRowRatio, () =>
                ReferenceTables.CopyToStagingAsync(connection, "hrsa_health_center_site", ReferenceTables.ToDataTable(sites,
                    ("site_id", typeof(string), s => s.SiteId), ("name", typeof(string), s => s.Name), ("address", typeof(string), s => s.Address),
                    ("city", typeof(string), s => s.City), ("state", typeof(string), s => s.State), ("zip5", typeof(string), s => s.Zip5),
                    ("phone", typeof(string), s => s.Phone), ("website", typeof(string), s => s.Website), ("hours_per_week", typeof(double), s => s.HoursPerWeek),
                    ("setting", typeof(string), s => s.Setting), ("location_type", typeof(string), s => s.LocationType), ("site_type", typeof(string), s => s.SiteType),
                    ("look_alike", typeof(sbyte), s => (sbyte)(s.LookAlike ? 1 : 0)), ("grant_number", typeof(string), s => s.GrantNumber),
                    ("health_center", typeof(string), s => s.HealthCenter), ("health_center_city", typeof(string), s => s.HealthCenterCity),
                    ("health_center_state", typeof(string), s => s.HealthCenterState), ("ccn", typeof(string), s => s.Ccn), ("npi", typeof(string), s => s.Npi),
                    ("added_date", typeof(DateTime), s => s.AddedDate), ("county_fips", typeof(string), s => s.CountyFips),
                    ("lat", typeof(double), s => s.Latitude), ("lon", typeof(double), s => s.Longitude)), ct), ct);
            context.Log.Information("HRSA health center sites: {Sites:N0} active service sites ({WithCcn:N0} with a Medicare number, {WithNpi:N0} with an NPI)",
                counts["hrsa_health_center_site"], sites.Count(s => s.Ccn is not null), sites.Count(s => s.Npi is not null));
            return counts["hrsa_health_center_site"];
        }, ct);
}
