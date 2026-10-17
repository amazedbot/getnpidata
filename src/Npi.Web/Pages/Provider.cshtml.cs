using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Npi.Core.Search;

namespace Npi.Web.Pages;

/// <summary>A status chip in the profile's summary card.</summary>
public sealed record StatusPill(string Text, string Kind, string? Title = null);

/// <summary>
/// /provider/{npi}: the profile (docs/PRODUCT_PLAN.md step 1.4): a summary card with an avatar, a one-sentence summary and the
/// status; a map preview; the specialty in plain words; then sections in the order people ask (where, what, who with, standing,
/// Medicare, quality, payments, changes, identifiers). A deactivated NPI gets only "Deactivated on [date]" (owner, 2026-10-11).
/// </summary>
public class ProviderModel(ProviderDetailService details, OrganizationService organizations, IOptions<MapOptions> mapOptions) : PageModel
{
    public ProviderDetail Provider { get; private set; } = null!;

    /// <summary>Set for a deactivated NPI; the page then shows only its status.</summary>
    public bool Deactivated { get; private set; }

    public DateOnly? DeactivationDate { get; private set; }

    /// <summary>A deactivated NPI's last record listed active in the archived monthly files (no name), when there is one.</summary>
    public ProviderHistoryVersion? LastKnown { get; private set; }

    /// <summary>The archive's months for <see cref="LastKnown"/>'s source note.</summary>
    public ProviderHistory? DeactivatedHistory { get; private set; }

    /// <summary>The versions shown before "Show all" in "Over the years".</summary>
    public const int HistoryShown = 4;

    public string Npi { get; private set; } = "";

    /// <summary>The primary specialty in the site's plain categories; null without a primary taxonomy.</summary>
    public SpecialtyPlacement? Placement { get; private set; }

    public SpecialtyCategory? Category { get; private set; }

    public SpecialtySubcategory? Subcategory { get; private set; }

    public ProviderTaxonomy? PrimaryTaxonomy { get; private set; }

    public StaticMap? Map { get; private set; }

    /// <summary>The search or map page the visitor came from, for "Back to results".</summary>
    public string? BackLink { get; private set; }

    public string Attribution => mapOptions.Value.Attribution;

    /// <summary>The Medicare groups this organization NPI is enrolled as (step 1.5), largest first.</summary>
    public IReadOnlyList<OrganizationGroup> Groups { get; private set; } = [];

    /// <summary>The largest group's clinicians, for an organization.</summary>
    public GroupProfile? OrganizationClinicians { get; private set; }

    /// <summary>The groups a clinician bills Medicare through that Care Compare doesn't already list.</summary>
    public IReadOnlyList<OrganizationGroup> BillsThrough { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string npi, CancellationToken ct)
    {
        Npi = npi;
        var provider = await details.GetAsync(npi, ct);
        if (provider is null)
        {
            var (found, date) = await details.GetDeactivationAsync(npi, ct);
            if (!found)
            {
                return NotFound(); // unknown or malformed
            }

            Deactivated = true;
            DeactivationDate = date;
            DeactivatedHistory = await details.GetHistoryAsync(npi, ct);
            LastKnown = DeactivatedHistory?.LastActive;
            return Page();
        }

        Provider = provider;
        PrimaryTaxonomy = provider.Taxonomies.FirstOrDefault(t => t.IsPrimary) ?? (provider.Taxonomies.Count > 0 ? provider.Taxonomies[0] : null);
        Placement = SpecialtyCategories.Default.Place(PrimaryTaxonomy?.Code);
        Category = SpecialtyCategories.Default.Find(Placement?.Category);
        Subcategory = Category?.Find(Placement?.Subcategory);
        Map = mapOptions.Value.Enabled
            ? StaticMap.Create(provider.Locations.Select((l, i) => (Label: (i + 1).ToString(CultureInfo.InvariantCulture), l.Latitude, l.Longitude, l.Approximate))
                .Where(p => p.Latitude is not null && p.Longitude is not null).Select(p => (p.Label, p.Latitude!.Value, p.Longitude!.Value, p.Approximate)),
                mapOptions.Value.TileUrl)
            : null;
        if (provider.EntityType == 2)
        {
            Groups = await organizations.GetGroupsForNpiAsync(npi, ct);
            if (Groups.Count > 0)
            {
                OrganizationClinicians = await organizations.GetGroupAsync(Groups[0].PacId, 1, ct);
            }
        }
        else
        {
            var listed = provider.CareCompare?.GroupPractices.Select(g => g.OrgPacId).ToHashSet() ?? [];
            BillsThrough = [.. (await organizations.GetGroupsForClinicianAsync(npi, ct)).Where(g => !listed.Contains(g.PacId))];
        }

        BackLink = Request.Headers.Referer.FirstOrDefault() is { } referer && Uri.TryCreate(referer, UriKind.Absolute, out var from)
                   && string.Equals(from.Host, Request.Host.Host, StringComparison.OrdinalIgnoreCase)
                   && from.AbsolutePath is "/" or "/search" or "/map"
                   && from.Query.Length > 1
            ? from.PathAndQuery
            : null;
        return Page();
    }

    public bool IsPerson => Provider.EntityType == 1;

    /// <summary>"Jordan A Ellis" from "ELLIS, JORDAN A"; an organization keeps its legal name.</summary>
    public string DisplayName
    {
        get
        {
            if (!IsPerson)
            {
                return Provider.Name;
            }

            var parts = Provider.Name.Split(',', 2, StringSplitOptions.TrimEntries);
            var natural = parts.Length == 2 && parts[1].Length > 0 ? $"{parts[1]} {parts[0]}" : Provider.Name;
            return CityCatalog.TitleCase(natural);
        }
    }

    /// <summary>"Chiropractor", from the plain categories, else NUCC's classification.</summary>
    public string SpecialtyName => Placement is { } p && p.Category != SpecialtyCategories.FallbackCategory
        ? p.Singular
        : PrimaryTaxonomy?.Classification ?? (IsPerson ? "Health care provider" : "Health care organization");

    /// <summary>"What a chiropractor does", "What an ENT doctor does"; for an organization, "About this kind of provider".</summary>
    public string WhatLabel
    {
        get
        {
            if (!IsPerson)
            {
                return "About this kind of provider";
            }

            var name = SpecialtyName;
            // Lower-case the first letter unless it starts an abbreviation ("ENT doctor", "OB/GYN").
            var shown = name.Length > 1 && char.IsUpper(name[1]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
            var article = "aeiouAEIOU".Contains(name[0], StringComparison.Ordinal) ? "an" : "a";
            return $"What {article} {shown} does";
        }
    }

    public ProviderLocation? PrimaryLocation => Provider.Locations.FirstOrDefault(l => l.IsPrimary) ?? (Provider.Locations.Count > 0 ? Provider.Locations[0] : null);

    /// <summary>"Babylon, NY", title-cased.</summary>
    public static string Place(ProviderLocation? l) =>
        l?.City is null ? "" : l.State is null ? CityCatalog.TitleCase(l.City) : $"{CityCatalog.TitleCase(l.City)}, {l.State}";

    /// <summary>The largest Care Compare group the clinician belongs to.</summary>
    public GroupPractice? MainGroup => Provider.CareCompare?.GroupPractices.Where(g => g.Members is > 1).OrderByDescending(g => g.Members).FirstOrDefault();

    /// <summary>"Chiropractor in Babylon, NY since 2007, part of a 6-clinician group."</summary>
    public string Summary
    {
        get
        {
            var text = SpecialtyName;
            if (Place(PrimaryLocation) is { Length: > 0 } place)
            {
                text += " in " + place;
            }

            if (IsPerson && Provider.EnumerationDate is { } since)
            {
                text += $" since {since.Year.ToString(CultureInfo.InvariantCulture)}";
            }

            if (IsPerson)
            {
                if (MainGroup is { Members: { } members })
                {
                    text += $", part of a {members.ToString("N0", CultureInfo.InvariantCulture)}-clinician group";
                }

                return text + ".";
            }

            // "Chiropractic group in Babylon, NY with 6 clinicians at 2 locations, registered since 2012."
            var parts = new List<string>();
            if (Groups.Count > 0 && Groups.Sum(g => g.Clinicians) is var clinicians and > 0)
            {
                parts.Add(clinicians == 1 ? "1 clinician" : $"{clinicians.ToString("N0", CultureInfo.InvariantCulture)} clinicians");
            }

            if (Provider.Locations.Count > 1)
            {
                parts.Add($"{Provider.Locations.Count.ToString(CultureInfo.InvariantCulture)} locations");
            }

            var org = SpecialtyName;
            if (Place(PrimaryLocation) is { Length: > 0 } where)
            {
                org += " in " + where;
            }

            if (parts.Count > 0)
            {
                org += " with " + string.Join(" at ", parts);
            }

            if (Provider.EnumerationDate is { } registered)
            {
                org += $", registered since {registered.Year.ToString(CultureInfo.InvariantCulture)}";
            }

            return org + ".";
        }
    }

    /// <summary>Board actions or a discipline flag in a state's records.</summary>
    public bool HasBoardAction => Provider.StateLicenses.Any(l => l.Actions.Count > 0
        || l.Discipline?.Trim().ToUpperInvariant() is "Y" or "YES");

    /// <summary>Medicare billing or prescribing in the newest data year or an older one: the Medicare activity section.</summary>
    public bool HasMedicare => Provider.MedicareServices is not null || Provider.MedicarePrescribing is not null || Provider.MedicareHistory is not null;

    public bool OptedOut => Provider.Compliance.OptOut is { Active: true };

    /// <summary>The record-quality explanation (docs/PRODUCT_PLAN.md step 3.1); empty when the record isn't labelled yet.</summary>
    public IReadOnlyList<QualityNote> QualityNotes => Provider.Quality is { } q
        ? RecordQuality.Explain(q, Provider.LastUpdateDate, DateOnly.FromDateTime(DateTime.UtcNow), IsPerson, Provider.QualityCareCompare,
            Provider.MedicareHistory)
        : [];

    public IEnumerable<StatusPill> Status
    {
        get
        {
            yield return new StatusPill("Active NPI", "ok");
            if (Provider.Compliance.Exclusions.Count > 0)
            {
                yield return new StatusPill("Excluded by HHS-OIG", "alert", "On the federal List of Excluded Individuals/Entities");
            }

            if (OptedOut)
            {
                yield return new StatusPill("Opted out of Medicare", "warn");
            }

            if (HasBoardAction)
            {
                yield return new StatusPill("State board action on record", "warn");
            }

            if (Provider.Compliance.Exclusions.Count == 0 && !OptedOut && !HasBoardAction)
            {
                yield return new StatusPill("No exclusions or opt-outs", "ok");
            }

            if (Provider.CareCompare is { AcceptsMedicareAssignment: true })
            {
                yield return new StatusPill("Accepts Medicare", "plain", "Accepts Medicare's approved amount as full payment (Care Compare)");
            }

            if (Provider.CareCompare is { OffersTelehealth: true })
            {
                yield return new StatusPill("Telehealth", "plain");
            }

            if (Provider.Quality is { } quality)
            {
                var reasons = quality.ReasonTexts.Where(r => r is not ("NPI reactivated" or "Home address")).ToList();
                yield return quality.Label switch
                {
                    QualityLabel.Stale => new StatusPill("Record looks stale", "alert", string.Join("; ", reasons)),
                    QualityLabel.Check => new StatusPill("Record needs a check", "warn", string.Join("; ", reasons)),
                    _ => new StatusPill("Record looks current", "ok", "No conflicts or gaps found in the record (see Record quality)"),
                };
            }

            if (Provider.LastUpdateDate is { } updated)
            {
                yield return new StatusPill("Updated " + Format.Ago(updated), "plain",
                    "NPPES record last updated " + updated.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));
            }
        }
    }

    public bool HasAffiliations => Provider.CareCompare is { } cc && (cc.GroupPractices.Count > 0 || cc.Facilities.Count > 0) || Provider.Facilities.Count > 0
                                   || BillsThrough.Count > 0;

    /// <summary>An organization's clinicians: the first ten of its largest group, with a link to the group page.</summary>
    public ClinicianListView? ClinicianList => OrganizationClinicians is { } g
        ? new ClinicianListView(Groups.Count > 1 ? $"Clinicians of {CityCatalog.TitleCase(g.Name)}" : "Clinicians", g.Clinicians, $"/group/{g.PacId}",
            $"/group/{g.PacId}/clinicians.csv",
            "Clinicians who bill Medicare through this organization's Medicare group (enrollment reassignments) or list it in Medicare Care Compare.",
            Limit: 10, AllLink: $"/group/{g.PacId}")
        : null;

    /// <summary>The page's sections that have content, for the "On this page" links.</summary>
    public IEnumerable<(string Id, string Title)> Sections
    {
        get
        {
            if (OrganizationClinicians is not null)
            {
                yield return ("clinicians", "Clinicians");
            }

            yield return ("where", "Locations");
            yield return ("specialties", "Specialties & licenses");
            if (HasAffiliations)
            {
                yield return ("works-with", IsPerson ? "Works with" : "Facilities");
            }

            yield return ("standing", "Standing");
            if (Provider.Quality is not null)
            {
                yield return ("record-quality", "Record quality");
            }

            if (HasMedicare)
            {
                yield return ("medicare", "Medicare");
            }

            if (Provider.MedicaidHistory is not null)
            {
                yield return ("medicaid", "Medicaid");
            }

            if (Provider.MipsScores.Count > 0)
            {
                yield return ("quality", "Quality");
            }

            if (Provider.IndustryPayments is not null || Provider.PaymentHistory is not null)
            {
                yield return ("payments", "Industry payments");
            }

            if (Provider.Changes.Count > 0)
            {
                yield return ("changes", "Changes");
            }

            yield return ("record", "Registration & identifiers");
        }
    }

    /// <summary>The full map page around the practice locations, showing only this provider.</summary>
    public string MapLink
    {
        get
        {
            var points = Provider.Locations.Where(l => l.Latitude is not null && l.Longitude is not null).ToList();
            if (points.Count == 0)
            {
                return "/map?npi=" + Provider.Npi;
            }

            static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            const double pad = 0.02;
            return $"/map?npi={Provider.Npi}&bbox={F(points.Min(l => l.Longitude!.Value) - pad)},{F(points.Min(l => l.Latitude!.Value) - pad)},"
                   + $"{F(points.Max(l => l.Longitude!.Value) + pad)},{F(points.Max(l => l.Latitude!.Value) + pad)}";
        }
    }

    /// <summary>Directions to a location on OpenStreetMap.</summary>
    public static string? Directions(ProviderLocation l) =>
        l.Latitude is { } lat && l.Longitude is { } lon
            ? "https://www.openstreetmap.org/directions?route=%3B" + Uri.EscapeDataString(
                $"{lat.ToString("0.#####", CultureInfo.InvariantCulture)},{lon.ToString("0.#####", CultureInfo.InvariantCulture)}")
            : null;

    /// <summary>The plain name of any taxonomy code ("Cardiologist"), or null for codes outside the plain categories.</summary>
    public static string? PlainName(string code) =>
        SpecialtyCategories.Default.Place(code) is { } p && p.Category != SpecialtyCategories.FallbackCategory ? p.Singular : null;
}
