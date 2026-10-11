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
public class ProviderModel(ProviderDetailService details, IOptions<MapOptions> mapOptions) : PageModel
{
    public ProviderDetail Provider { get; private set; } = null!;

    /// <summary>Set for a deactivated NPI; the page then shows only its status.</summary>
    public bool Deactivated { get; private set; }

    public DateOnly? DeactivationDate { get; private set; }

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

            if (Provider.EnumerationDate is { } since)
            {
                text += $" since {since.Year.ToString(CultureInfo.InvariantCulture)}";
            }

            if (IsPerson && MainGroup is { Members: { } members })
            {
                text += $", part of a {members.ToString("N0", CultureInfo.InvariantCulture)}-clinician group";
            }
            else if (!IsPerson && Provider.Locations.Count > 1)
            {
                text += $", {Provider.Locations.Count.ToString(CultureInfo.InvariantCulture)} practice locations";
            }

            return text + ".";
        }
    }

    /// <summary>Board actions or a discipline flag in a state's records.</summary>
    public bool HasBoardAction => Provider.StateLicenses.Any(l => l.Actions.Count > 0
        || l.Discipline?.Trim().ToUpperInvariant() is "Y" or "YES");

    public bool OptedOut => Provider.Compliance.OptOut is { Active: true };

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

            if (Provider.LastUpdateDate is { } updated)
            {
                yield return new StatusPill("Updated " + Format.Ago(updated), "plain",
                    "NPPES record last updated " + updated.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));
            }
        }
    }

    public bool HasAffiliations => Provider.CareCompare is { } cc && (cc.GroupPractices.Count > 0 || cc.Facilities.Count > 0) || Provider.Facilities.Count > 0;

    /// <summary>The page's sections that have content, for the "On this page" links.</summary>
    public IEnumerable<(string Id, string Title)> Sections
    {
        get
        {
            yield return ("where", "Locations");
            yield return ("specialties", "Specialties & licenses");
            if (HasAffiliations)
            {
                yield return ("works-with", IsPerson ? "Works with" : "Facilities");
            }

            yield return ("standing", "Standing");
            if (Provider.MedicareServices is not null || Provider.MedicarePrescribing is not null)
            {
                yield return ("medicare", "Medicare");
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
