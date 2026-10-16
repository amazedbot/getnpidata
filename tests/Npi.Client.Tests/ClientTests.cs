using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Npi.Web.Search;
using Server = Npi.Core.Search;

namespace Npi.Client.Tests;

/// <summary>
/// The typed client against the real API (in-memory, no database: the connection string points at a
/// closed port), plus contract checks that the client's parameters and models match the server's.
/// </summary>
public class ClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ApiFactory(IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }
        }
    }

    private static ProviderSearch EverySearchProperty() => new()
    {
        Category = "Doctors (MD, DO)", Subcategory = "Heart (cardiology)",
        Classification = "Chiropractor", Specialization = "Sports Physician", Taxonomy = "111NS0005X", State = "NY", County = "36103",
        City = "Babylon", Zip = "11702", Radius = 10, LastName = "o'brien & co", FirstName = "jose", OrgName = "acme", NameMatch = "similar", Npi = "1234567893",
        EntityType = 1, Gender = "F", Credential = "MD", Excluded = false, OptedOut = true, OrderRefer = true, AcceptsAssignment = true, Telehealth = false,
        MinYears = 10, MedicareActive = true, Shortage = "dental", AddressType = "residential", Bbox = "-74,40.7,-73.9,40.8", Quality = "notStale", NewWithinDays = 30, UpdatedWithinDays = 7, AllNpis = true, Sort = "-city",
        Page = 3, PageSize = 100,
    };

    [Fact]
    public void Query_string_contains_only_set_properties_and_escapes_values()
    {
        Assert.Equal("", new ProviderSearch().ToQueryString());
        Assert.Equal("?state=NY&lastName=o%27brien%20%26%20co", new ProviderSearch { State = " NY ", LastName = "o'brien & co", City = " " }.ToQueryString());
    }

    [Fact]
    public void Every_client_parameter_is_understood_by_the_server_parser()
    {
        var query = new QueryCollection(QueryHelpers.ParseQuery(EverySearchProperty().ToQueryString()));
        var (filter, errors) = SearchQueryString.Parse(query);

        Assert.Empty(errors);
        Assert.Equal(new Server.SearchFilter
        {
            Category = "Doctors (MD, DO)", Subcategory = "Heart (cardiology)",
            Classification = "Chiropractor", Specialization = "Sports Physician", TaxonomyCode = "111NS0005X", State = "NY", CountyFips = "36103",
            City = "Babylon", Zip5 = "11702", RadiusMiles = 10, LastName = "o'brien & co", FirstName = "jose", OrgName = "acme", NameMatch = "similar", Npi = "1234567893",
            EntityType = 1, Gender = "F", Credential = "MD", Excluded = false, OptedOut = true, OrderRefer = true, AcceptsAssignment = true,
            Telehealth = false, MinYears = 10, MedicareActive = true, Shortage = "dental", AddressType = "residential", Bbox = "-74,40.7,-73.9,40.8", Quality = "notStale", NewWithinDays = 30,
            UpdatedWithinDays = 7, AllNpis = true, Sort = "-city", Page = 3, PageSize = 100,
        }, filter);

        // And the client covers every parameter the server documents.
        Assert.Equal(SearchQueryString.Parameters.Count, query.Count);
    }

    [Theory]
    [InlineData(typeof(Server.SearchResult), typeof(ProviderPage))]
    [InlineData(typeof(Server.CredentialInfo), typeof(CredentialInfo))]
    [InlineData(typeof(Server.ProviderSummary), typeof(ProviderSummary))]
    [InlineData(typeof(Server.ProviderDetail), typeof(ProviderDetail))]
    [InlineData(typeof(Server.ProviderTaxonomy), typeof(ProviderTaxonomy))]
    [InlineData(typeof(Server.ProviderLocation), typeof(ProviderLocation))]
    [InlineData(typeof(Server.StateInfo), typeof(StateInfo))]
    [InlineData(typeof(Server.CountyInfo), typeof(CountyInfo))]
    [InlineData(typeof(Npi.Web.Api.ApiMeta), typeof(ApiMeta))]
    [InlineData(typeof(Server.ProviderProfile), typeof(ProviderProfile))]
    [InlineData(typeof(Server.AuthorizedOfficial), typeof(AuthorizedOfficial))]
    [InlineData(typeof(Server.MailingAddress), typeof(MailingAddress))]
    [InlineData(typeof(Server.ProviderIdentifier), typeof(ProviderIdentifier))]
    [InlineData(typeof(Server.ProviderEndpoint), typeof(ProviderEndpoint))]
    [InlineData(typeof(Server.ProviderFlags), typeof(ProviderFlags))]
    [InlineData(typeof(Server.ProviderCompliance), typeof(ProviderCompliance))]
    [InlineData(typeof(Server.OigExclusion), typeof(OigExclusion))]
    [InlineData(typeof(Server.MedicareOptOut), typeof(MedicareOptOut))]
    [InlineData(typeof(Server.MedicareOrderReferring), typeof(MedicareOrderReferring))]
    [InlineData(typeof(Server.ProviderCareCompare), typeof(ProviderCareCompare))]
    [InlineData(typeof(Server.GroupPractice), typeof(GroupPractice))]
    [InlineData(typeof(Server.FacilityAffiliation), typeof(FacilityAffiliation))]
    [InlineData(typeof(Server.CertifiedFacility), typeof(CertifiedFacility))]
    [InlineData(typeof(Server.MedicareServices), typeof(MedicareServices))]
    [InlineData(typeof(Server.MedicareService), typeof(MedicareService))]
    [InlineData(typeof(Server.MedicarePrescribing), typeof(MedicarePrescribing))]
    [InlineData(typeof(Server.CountyFacts), typeof(CountyFacts))]
    [InlineData(typeof(Server.CountyShortage), typeof(CountyShortage))]
    [InlineData(typeof(Server.IndustryPayments), typeof(IndustryPayments))]
    [InlineData(typeof(Server.ProviderChange), typeof(ProviderChange))]
    [InlineData(typeof(Server.IndustryPaymentHistory), typeof(IndustryPaymentHistory))]
    [InlineData(typeof(Server.OutcomeCounts), typeof(OutcomeCounts))]
    [InlineData(typeof(Server.HospitalOutcomes), typeof(HospitalOutcomes))]
    [InlineData(typeof(Server.MipsScore), typeof(MipsScore))]
    [InlineData(typeof(Server.StateLicenseRecord), typeof(StateLicenseRecord))]
    [InlineData(typeof(Server.StateBoardAction), typeof(StateBoardAction))]
    [InlineData(typeof(Server.IndustryPaymentYear), typeof(IndustryPaymentYear))]
    [InlineData(typeof(Server.IndustryPaymentCompany), typeof(IndustryPaymentCompany))]
    [InlineData(typeof(Server.IndustryPaymentKind), typeof(IndustryPaymentKind))]
    [InlineData(typeof(Server.IndustryPayer), typeof(IndustryPayer))]
    [InlineData(typeof(Server.CompanyPage), typeof(CompanyPage))]
    [InlineData(typeof(Server.CompanySummary), typeof(CompanySummary))]
    [InlineData(typeof(Server.CompanyDetail), typeof(CompanyDetail))]
    [InlineData(typeof(Server.CompanyYear), typeof(CompanyYear))]
    [InlineData(typeof(Server.CompanyNature), typeof(CompanyNature))]
    [InlineData(typeof(Server.CompanyProduct), typeof(CompanyProduct))]
    [InlineData(typeof(Server.CompanySpecialty), typeof(CompanySpecialty))]
    [InlineData(typeof(Server.CompanyRecipient), typeof(CompanyRecipient))]
    [InlineData(typeof(Server.CompanyRecalls), typeof(CompanyRecalls))]
    [InlineData(typeof(Server.IndustryProduct), typeof(IndustryProduct))]
    [InlineData(typeof(Server.ProductPage), typeof(ProductPage))]
    [InlineData(typeof(Server.ProductSummary), typeof(ProductSummary))]
    [InlineData(typeof(Server.ProductDetail), typeof(ProductDetail))]
    [InlineData(typeof(Server.ProductCompany), typeof(ProductCompany))]
    [InlineData(typeof(Server.ProductNature), typeof(ProductNature))]
    [InlineData(typeof(Server.ProductSpecialty), typeof(ProductSpecialty))]
    [InlineData(typeof(Server.ProductRecipient), typeof(ProductRecipient))]
    [InlineData(typeof(Server.ProductResearch), typeof(ProductResearch))]
    [InlineData(typeof(Server.ProductStudy), typeof(ProductStudy))]
    [InlineData(typeof(Server.ProductDrugInfo), typeof(ProductDrugInfo))]
    [InlineData(typeof(Server.ProductDeviceInfo), typeof(ProductDeviceInfo))]
    [InlineData(typeof(Server.ProductPremarket), typeof(ProductPremarket))]
    [InlineData(typeof(Server.ProductSpending), typeof(ProductSpending))]
    [InlineData(typeof(Server.ProductPrice), typeof(ProductPrice))]
    [InlineData(typeof(Server.ProductShortage), typeof(ProductShortage))]
    [InlineData(typeof(Server.ProductRecalls), typeof(ProductRecalls))]
    [InlineData(typeof(Server.ProductAdverseEvents), typeof(ProductAdverseEvents))]
    [InlineData(typeof(Server.ProductTrials), typeof(ProductTrials))]
    [InlineData(typeof(Server.ProductPrescribing), typeof(ProductPrescribing))]
    [InlineData(typeof(Server.ProductPrescriberPage), typeof(ProductPrescriberPage))]
    [InlineData(typeof(Server.ProductPrescriber), typeof(ProductPrescriber))]
    [InlineData(typeof(Server.CompanyRecall), typeof(CompanyRecall))]
    [InlineData(typeof(Server.CompanySecListing), typeof(CompanySecListing))]
    [InlineData(typeof(Server.CompanyIntegrityAgreement), typeof(CompanyIntegrityAgreement))]
    [InlineData(typeof(Server.LookupRow), typeof(LookupRow))]
    [InlineData(typeof(Server.ProviderHistory), typeof(ProviderHistory))]
    [InlineData(typeof(Server.ProviderHistoryVersion), typeof(ProviderHistoryVersion))]
    [InlineData(typeof(Server.HistoryPlace), typeof(HistoryPlace))]
    [InlineData(typeof(Server.HistoryChange), typeof(HistoryChange))]
    [InlineData(typeof(Server.ProviderQuality), typeof(RecordQuality))]
    [InlineData(typeof(Server.ProviderOrganization), typeof(ProviderOrganization))]
    [InlineData(typeof(Server.OrganizationProfile), typeof(OrganizationProfile))]
    [InlineData(typeof(Server.OrganizationMember), typeof(OrganizationMember))]
    [InlineData(typeof(Server.OrganizationGroup), typeof(OrganizationGroup))]
    [InlineData(typeof(Server.MedicareHistory), typeof(MedicareHistory))]
    [InlineData(typeof(Server.MedicareHistoryYear), typeof(MedicareHistoryYear))]
    [InlineData(typeof(Npi.Web.Api.LookupResponse), typeof(LookupResponse))]
    [InlineData(typeof(Server.FacilityPage), typeof(FacilityPage))]
    [InlineData(typeof(Server.FacilitySummary), typeof(FacilitySummary))]
    [InlineData(typeof(Server.FacilityProfile), typeof(FacilityProfile))]
    [InlineData(typeof(Server.FacilityCertification), typeof(FacilityCertification))]
    [InlineData(typeof(Server.ClinicianList), typeof(ClinicianList))]
    [InlineData(typeof(Server.SpecialtyCount), typeof(SpecialtyCount))]
    [InlineData(typeof(Server.ClinicianRow), typeof(ClinicianRow))]
    [InlineData(typeof(Server.FacilityMeasure), typeof(FacilityMeasure))]
    [InlineData(typeof(Server.DialysisFacts), typeof(DialysisFacts))]
    [InlineData(typeof(Server.DialysisQualityIncentive), typeof(DialysisQualityIncentive))]
    [InlineData(typeof(Server.HospitalVolume), typeof(HospitalVolume))]
    [InlineData(typeof(Server.HospitalService), typeof(HospitalService))]
    [InlineData(typeof(Server.GroupPage), typeof(GroupPage))]
    [InlineData(typeof(Server.GroupSummary), typeof(GroupSummary))]
    [InlineData(typeof(Server.AreaHistory), typeof(AreaHistory))]
    [InlineData(typeof(Server.AreaMonth), typeof(AreaMonth))]
    [InlineData(typeof(Server.AreaHistoryCoverage), typeof(AreaHistoryCoverage))]
    [InlineData(typeof(Server.AreaMapData), typeof(AreaMapData))]
    [InlineData(typeof(Server.AreaMapValue), typeof(AreaMapValue))]
    [InlineData(typeof(Server.FacilityOwnership), typeof(FacilityOwnership))]
    [InlineData(typeof(Server.FacilityOwner), typeof(FacilityOwner))]
    [InlineData(typeof(Server.OwnershipChange), typeof(OwnershipChange))]
    [InlineData(typeof(Server.PreviousOwner), typeof(PreviousOwner))]
    [InlineData(typeof(Server.OwnerPage), typeof(OwnerPage))]
    [InlineData(typeof(Server.OwnerSummary), typeof(OwnerSummary))]
    [InlineData(typeof(Server.OwnerDetail), typeof(OwnerDetail))]
    [InlineData(typeof(Server.OwnerFacility), typeof(OwnerFacility))]
    [InlineData(typeof(Server.OwnerRole), typeof(OwnerRole))]
    [InlineData(typeof(Server.OrganizationNpi), typeof(OrganizationNpi))]
    [InlineData(typeof(Server.NursingHomeInspections), typeof(NursingHomeInspections))]
    [InlineData(typeof(Server.InspectionCount), typeof(InspectionCount))]
    [InlineData(typeof(Server.InspectionSurvey), typeof(InspectionSurvey))]
    [InlineData(typeof(Server.InspectionCitation), typeof(InspectionCitation))]
    [InlineData(typeof(Server.NursingHomePenalty), typeof(NursingHomePenalty))]
    [InlineData(typeof(Server.HealthSystemPage), typeof(HealthSystemPage))]
    [InlineData(typeof(Server.HealthSystemSummary), typeof(HealthSystemSummary))]
    [InlineData(typeof(Server.HealthSystemDetail), typeof(HealthSystemDetail))]
    [InlineData(typeof(Server.HealthSystemFacility), typeof(HealthSystemFacility))]
    [InlineData(typeof(Server.HealthSystemGroup), typeof(HealthSystemGroup))]
    [InlineData(typeof(Server.AcoPage), typeof(AcoPage))]
    [InlineData(typeof(Server.AcoSummary), typeof(AcoSummary))]
    [InlineData(typeof(Server.AcoDetail), typeof(AcoDetail))]
    [InlineData(typeof(Server.HealthCenterSite), typeof(HealthCenterSite))]
    [InlineData(typeof(Server.HealthCenterSiteDetail), typeof(HealthCenterSiteDetail))]
    [InlineData(typeof(Server.AcoParticipant), typeof(AcoParticipant))]
    [InlineData(typeof(Server.AcoParticipantMatch), typeof(AcoParticipantMatch))]
    [InlineData(typeof(Server.AcoMembership), typeof(AcoMembership))]
    [InlineData(typeof(Server.Covered340BEntity), typeof(Covered340BEntity))]
    public void Client_models_have_every_field_the_server_sends(Type server, Type client)
    {
        // The names as JSON writes them: [JsonIgnore] properties aren't sent, [JsonPropertyName] renames.
        static IEnumerable<string> Names(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract" && p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null)
                .Select(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name ?? p.Name);

        Assert.Empty(Names(server).Except(Names(client), StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Server_json_reads_into_client_models()
    {
        var server = new Server.ProviderDetail("1003000126", 1, "ENKESHAFI, ARDALAN", null, "M.D.", "M", new DateOnly(2007, 8, 31), new DateOnly(2025, 5, 28),
            [new Server.ProviderTaxonomy(4, "208M00000X", "Hospitalist", null, true, "123", "MD")],
            [new Server.ProviderLocation(true, "1 MAIN ST", null, "BETHESDA", "MD", "20814-0001", "US", "3015550100")],
            ["OTHER NAME"]);
        var json = JsonSerializer.Serialize(server, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var client = JsonSerializer.Deserialize<ProviderDetail>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("1003000126", client.Npi);
        Assert.Equal("Individual", client.EntityTypeName);
        Assert.Equal(new DateTime(2007, 8, 31), client.EnumerationDate);
        Assert.True(Assert.Single(client.Taxonomies).IsPrimary);
        Assert.Equal("20814-0001", Assert.Single(client.Locations).Zip);
        Assert.Equal(["OTHER NAME"], client.OtherNames);
    }

    [Fact]
    public void A_facility_profile_reads_into_the_client_models()
    {
        var server = new Server.FacilityProfile(
            new Server.CertifiedFacility("330045", "hospital", "GOOD SAMARITAN HOSPITAL", "Acute Care Hospitals", null, "WEST ISLIP", "NY", null, true, null, 3, null, null,
                null, 812),
            "1000 MONTAUK HWY", "11795", 40.7, -73.3, [new Server.OrganizationNpi("1000000038", "GOOD SAMARITAN HOSPITAL", "General hospital", "WEST ISLIP", "NY")],
            new Server.ClinicianList(1, [new Server.SpecialtyCount("Cardiologist", 1)],
                [new Server.ClinicianRow("1000000004", "JANE DOE", "MD", "F", "Cardiologist", "WEST ISLIP", "NY")], 1, 50))
        {
            Measures = [new Server.FacilityMeasure("MORT_30_AMI", Server.FacilityMeasures.Deaths, "Heart attack patients who died within 30 days", Server.MeasureUnits.Percent,
                false, 13.5, 11.9, Server.MeasureComparisons.Same, 234, new DateOnly(2022, 7, 1), new DateOnly(2025, 6, 30))],
            MedicareVolume = new Server.HospitalVolume(2024, 4299, 3177, 23739, 52228590, 65257547, 2024, 1200, 450000.5,
                [new Server.HospitalService("470", "MAJOR HIP AND KNEE JOINT REPLACEMENT", 300, null, 12000)], []),
        };
        var json = JsonSerializer.Serialize(server, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var client = JsonSerializer.Deserialize<FacilityProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(("330045", "hospital", 812, 1), (client.Facility.Ccn, client.Facility.Kind, client.Facility.AffiliatedClinicians, client.Clinicians.Pages));
        var measure = Assert.Single(client.Measures);
        Assert.Equal(("MORT_30_AMI", 13.5, 11.9, "same", new DateTime(2025, 6, 30)), (measure.Id, measure.Value, measure.National, measure.Comparison, measure.To));
        Assert.Equal((4299, "470", 300), (client.MedicareVolume!.Discharges, client.MedicareVolume.TopInpatient[0].Code, client.MedicareVolume.TopInpatient[0].Cases));
        Assert.Equal("JANE DOE", Assert.Single(client.Clinicians.Page).Name);
    }

    [Fact]
    public async Task An_unknown_facility_is_null()
    {
        await using var factory = new ApiFactory();
        using var npi = new NpiClient(factory.CreateClient());
        Assert.Null(await npi.GetFacilityAsync("not-a-ccn", cancellationToken: Ct)); // refused before the database
        await Assert.ThrowsAsync<ArgumentException>(() => npi.GetFacilityAsync(" ", cancellationToken: Ct));
    }

    [Fact]
    public void Facility_and_group_searches_send_every_parameter_the_server_takes()
    {
        // The server binds the list's parameters as handler arguments; the client must send each of them by the same name.
        static string[] ServerParameters(string method) =>
            [.. typeof(Npi.Web.Api.DirectoryEndpoints).GetMethod(method)!.GetParameters()
                .Where(p => p.ParameterType == typeof(string) || p.ParameterType == typeof(int?) || p.ParameterType == typeof(bool?)).Select(p => p.Name!)];

        static string[] Keys(string query) => [.. QueryHelpers.ParseQuery(query).Keys];

        var facilities = new FacilitySearch
        {
            Q = "good samaritan", Type = "hospital", State = "NY", MinRating = 3, MaxRating = 5, Status = "all", OwnershipChangedWithinYears = 3,
            HasPenalties = true, Sort = "-rating", System = "HSI00000010", In340B = true, Page = 2, PageSize = 100,
        };
        Assert.Equal(ServerParameters(nameof(Npi.Web.Api.DirectoryEndpoints.SearchFacilitiesAsync)), Keys(facilities.ToQueryString()));
        Assert.Equal("?q=good%20samaritan&type=hospital", new FacilitySearch { Q = " good samaritan ", Type = "hospital", State = " " }.ToQueryString());

        var groups = new GroupSearch { Q = "1234567893", State = "NY", MinClinicians = 10, MaxClinicians = 50, Sort = "name", Page = 2, PageSize = 10 };
        Assert.Equal(ServerParameters(nameof(Npi.Web.Api.DirectoryEndpoints.SearchGroupsAsync)), Keys(groups.ToQueryString()));
        Assert.Equal("", new GroupSearch().ToQueryString());
    }

    [Fact]
    public void System_and_aco_searches_send_every_parameter_the_server_takes()
    {
        static string[] ServerParameters(string method) =>
            [.. typeof(Npi.Web.Api.SystemEndpoints).GetMethod(method)!.GetParameters()
                .Where(p => p.ParameterType == typeof(string) || p.ParameterType == typeof(int?) || p.ParameterType == typeof(bool?)).Select(p => p.Name!)];

        static string[] Keys(string query) => [.. QueryHelpers.ParseQuery(query).Keys];

        var systems = new SystemSearch { Q = "harbor", State = "NY", Sort = "-beds", Page = 2, PageSize = 20 };
        Assert.Equal(ServerParameters(nameof(Npi.Web.Api.SystemEndpoints.SearchSystemsAsync)), Keys(systems.ToQueryString()));
        var acos = new AcoSearch { Q = "harbor", State = "NY", Page = 2, PageSize = 20 };
        Assert.Equal(ServerParameters(nameof(Npi.Web.Api.SystemEndpoints.SearchAcosAsync)), Keys(acos.ToQueryString()));
        Assert.Equal("?q=a%20b", new AcoSearch { Q = " a b ", State = " " }.ToQueryString());
    }

    [Fact]
    public void System_aco_and_site_json_reads_into_client_models()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var system = new Server.HealthSystemDetail(
            new Server.HealthSystemSummary("HSI90000001", "Harbor Health System", "West Islip", "NY", 2023, 1200, 400, 300, 80, 45, 3, 1, 1, 812, 40210, true, null, false, 3),
            ["NY"], [new Server.HealthSystemFacility("330045", "hospital", "GOOD SAMARITAN HOSPITAL", "WEST ISLIP", "NY", true, 3)],
            [new Server.HealthSystemGroup("1000000001", "BIG HEALTH GROUP", "NY", 1200)]);
        var s = JsonSerializer.Deserialize<HealthSystemDetail>(JsonSerializer.Serialize(system, options), options)!;
        Assert.Equal(("HSI90000001", 2023, (bool?)null, 3, "330045", true, 1200),
            (s.System.SystemId, s.System.DataYear, s.System.OffersInsurance, s.System.LinkedFacilities, s.Facilities[0].Ccn, s.Facilities[0].Active, s.Groups[0].Clinicians));

        var aco = new Server.AcoDetail(new Server.AcoSummary("A9001", "Harbor ACO", 2026, "NY", "ENHANCED", 3, new DateOnly(2016, 1, 1), null, 2, 1, "www.example.org"),
            "1 Main St", 40.7, -73.3,
            [new Server.AcoParticipant("HARBOR MEDICAL GROUP PC", [new Server.AcoParticipantMatch("1234567893", "HARBOR MEDICAL GROUP PC", "WEST ISLIP", "NY")]),
                new Server.AcoParticipant("GOOD SAMARITAN HOSPITAL", [])],
            ["SHORE VIEW CARE LLC"]);
        var a = JsonSerializer.Deserialize<AcoDetail>(JsonSerializer.Serialize(aco, options), options)!;
        Assert.Equal(("A9001", new DateTime(2016, 1, 1), (DateTime?)null, "HARBOR MEDICAL GROUP PC", "SHORE VIEW CARE LLC"),
            (a.Aco.AcoId, a.Aco.InitialStartDate, a.Aco.CurrentStartDate, a.Participants[0].Name, a.SnfAffiliates[0]));
        Assert.Equal(("name", "1234567893", (string?)null, 0), (a.Participants[0].MatchedBy, a.Participants[0].Organizations[0].Npi, a.Participants[1].MatchedBy,
            a.Participants[1].Organizations.Count));

        var membership = new Server.AcoMembership("A9001", "Harbor ACO", 2026, "HARBOR MEDICAL GROUP PC", "1234567893", 2);
        var m = JsonSerializer.Deserialize<AcoMembership[]>(JsonSerializer.Serialize(new[] { membership }, options), options)!.Single();
        Assert.Equal(("A9001", 2026, 2, "name"), (m.AcoId, m.ProgramYear, m.Candidates, m.MatchedBy));

        var site = new Server.HealthCenterSite("BPS-LAL-900004", "Valley Mobile Clinic", "1 Farm Rd", "Albany", "NY", "12201", "5185550102", null, 20, "School",
            "Mobile Van", "Service Delivery Site", true, "LAL9000001", "VALLEY CARE", "Albany", "NY", null, null, new DateOnly(2006, 1, 1), 42.65, -73.75);
        var detail = new Server.HealthCenterSiteDetail(site, false, null, [], 0)
        {
            Covered340B = [new Server.Covered340BEntity("FQHCLA900001", "FQHCLA", "FQHC look-alike", true, new DateOnly(2015, 4, 1), null, "VALLEY CARE", null, "site")],
        };
        var d = JsonSerializer.Deserialize<HealthCenterSiteDetail>(JsonSerializer.Serialize(detail, options), options)!;
        Assert.Equal(("BPS-LAL-900004", true, 20.0, new DateTime(2006, 1, 1)), (d.Site.SiteId, d.Site.LookAlike, d.Site.HoursPerWeek, d.Site.AddedDate));
        Assert.Equal(("FQHCLA900001", "FQHCLA", true, new DateTime(2015, 4, 1), "site"),
            (d.Covered340B[0].Id340B, d.Covered340B[0].EntityType, d.Covered340B[0].Participating, d.Covered340B[0].StartDate, d.Covered340B[0].MatchedBy));
    }

    private sealed class DirectoryFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:RemoteMySql", "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");
            builder.ConfigureServices(s =>
            {
                s.AddSingleton(new Npi.Core.Search.FacilityDirectory(Npi.Core.Search.FacilityDirectory.Merge(
                    [new("331850", "HEALTHY COMMUNITY CENTER", "5 MAIN ST", "BROOKLYN", "NY", "11201", true, null, null),
                     new("330888", "OLD NAME HOSPITAL", "10 COURT ST", "BROOKLYN", "NY", "11201", false, "01", new DateOnly(2020, 1, 1))],
                    [], [], new Dictionary<string, (double Lat, double Lon)> { ["11201"] = (40.69, -73.99) })));
                s.AddSingleton(new Npi.Core.Search.OrganizationDirectory(
                    [new("1000000001", "BIG HEALTH GROUP", "NY", 1200, 3, true, 91.25, 2024)], [], [], null,
                    new Dictionary<string, IReadOnlyList<string>>()));
            });
        }
    }

    [Fact]
    public async Task Facilities_and_groups_read_into_client_models()
    {
        await using var factory = new DirectoryFactory();
        using var npi = new NpiClient(factory.CreateClient());

        var closed = await npi.SearchFacilitiesAsync(new FacilitySearch { Status = "closed" }, Ct);
        var hospital = Assert.Single(closed.Items);
        Assert.Equal(("330888", "hospital", "Short-term hospital", false, new DateTime(2020, 1, 1), "Closed or merged", 40.69),
            (hospital.Ccn, hospital.Type, hospital.Kind, hospital.Active, hospital.TerminationDate, hospital.TerminationReason, hospital.Latitude));

        var groups = await npi.SearchGroupsAsync(new GroupSearch { MinClinicians = 100 }, Ct);
        Assert.Equal((1, "1000000001", 91.25, true), (groups.TotalCount, groups.Items[0].PacId, groups.Items[0].MipsScore, groups.Items[0].AcceptsMedicare));

        using var csv = new MemoryStream();
        await npi.DownloadFacilitiesCsvAsync(new FacilitySearch { Type = "fqhc" }, csv, Ct);
        Assert.Contains("331850,HEALTHY COMMUNITY CENTER", System.Text.Encoding.UTF8.GetString(csv.ToArray()), StringComparison.Ordinal);
        using var groupCsv = new MemoryStream();
        await npi.DownloadGroupsCsvAsync(new GroupSearch(), groupCsv, Ct);
        Assert.Contains("1000000001,BIG HEALTH GROUP", System.Text.Encoding.UTF8.GetString(groupCsv.ToArray()), StringComparison.Ordinal);

        var bad = await Assert.ThrowsAsync<NpiApiException>(() => npi.SearchFacilitiesAsync(new FacilitySearch { Type = "spa", MinRating = 7 }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("type", bad.Errors.Keys);
        Assert.Contains("minRating", bad.Errors.Keys);
    }

    [Fact]
    public async Task Invalid_search_throws_with_errors_by_parameter_name()
    {
        await using var factory = new ApiFactory();
        using var npi = new NpiClient(factory.CreateClient());

        var badNpi = await Assert.ThrowsAsync<NpiApiException>(() => npi.SearchProvidersAsync(new ProviderSearch { Npi = "123" }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, badNpi.StatusCode);
        Assert.True(badNpi.Errors.ContainsKey("npi"));

        var radius = await Assert.ThrowsAsync<NpiApiException>(() => npi.SearchProvidersAsync(new ProviderSearch { Zip = "11701", Radius = 500 }, Ct));
        Assert.Contains("radius", radius.Errors.Keys);
        Assert.Contains("[radius]", radius.Message, StringComparison.Ordinal);

        using var csv = new MemoryStream();
        var badCsv = await Assert.ThrowsAsync<NpiApiException>(() => npi.DownloadProvidersCsvAsync(new ProviderSearch { Zip = "117" }, csv, Ct));
        Assert.Contains("zip", badCsv.Errors.Keys);
        Assert.Equal(0, csv.Length);
    }

    [Fact]
    public async Task Specialty_categories_and_their_subcategories()
    {
        await using var factory = new ApiFactory();
        using var npi = new NpiClient(factory.CreateClient());
        Assert.Contains("Nurse practitioners", await npi.GetCategoriesAsync(Ct));
        Assert.Contains("Family & primary care", await npi.GetSubcategoriesAsync("Nurse practitioners", Ct));
        var unknown = await Assert.ThrowsAsync<NpiApiException>(() => npi.GetSubcategoriesAsync("nope", Ct));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Area_history_and_map_requests_are_checked_by_the_server()
    {
        await using var factory = new ApiFactory();
        using var npi = new NpiClient(factory.CreateClient());

        var category = await Assert.ThrowsAsync<NpiApiException>(() => npi.GetAreaHistoryAsync("NY", "36103", category: "Wizards", cancellationToken: Ct));
        Assert.Equal(HttpStatusCode.BadRequest, category.StatusCode);
        Assert.Contains("category", category.Errors.Keys);

        var level = await Assert.ThrowsAsync<NpiApiException>(() => npi.GetAreaMapAsync("nation", cancellationToken: Ct));
        Assert.Contains("level", level.Errors.Keys);
        var stat = await Assert.ThrowsAsync<NpiApiException>(() => npi.GetAreaMapAsync("county", "bogus", state: "NY", month: new DateTime(2026, 10, 1), cancellationToken: Ct));
        Assert.Contains("stat", stat.Errors.Keys);
        await Assert.ThrowsAsync<ArgumentException>(() => npi.GetAreaHistoryAsync(county: "36103", cancellationToken: Ct));
    }

    [Fact]
    public void Area_history_json_reads_into_client_models()
    {
        var server = new Server.AreaHistory("county", "36103", "Suffolk County", "NY", "",
            [new Server.AreaMonth(new DateOnly(2026, 10, 1), 100, 4, 1, 2, 0, 3, 0, 1, 60, 20, 20) { NetChange = 3, Partial = true, Basis = "recorded" }],
            new Server.AreaHistoryCoverage(new DateOnly(2021, 11, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 11)));
        var json = JsonSerializer.Serialize(server, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var client = JsonSerializer.Deserialize<AreaHistory>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var month = Assert.Single(client.Months);
        Assert.Equal((new DateTime(2026, 10, 1), 100, 2, 3, true, "recorded"), (month.Month, month.Providers!.Value, month.MovedIn!.Value, month.NetChange!.Value, month.Partial, month.Basis));
        Assert.Equal(new DateTime(2021, 11, 1), client.Coverage.DerivedFrom);
    }

    [Fact]
    public void Ownership_and_inspection_json_reads_into_client_models()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var server = new Server.FacilityOwnership("330045", "O20020101000001", "GOOD SAMARITAN HOSPITAL", "1234567891", new DateOnly(2026, 8, 31),
            [new Server.FacilityOwner("0446239073", true, "SAMPLE HOSPITAL PARTNERS, LP", null, null, "34", "Direct owner (5% or more)", Server.OwnerRoles.Owner, 100,
                new DateOnly(2025, 3, 1), ["private_equity"], "NEW YORK", "NY")],
            new DateOnly(2026, 6, 30),
            [new Server.OwnershipChange(new DateOnly(2018, 6, 1), "Change of ownership", "hospital", "GOOD SAMARITAN HOSPITAL", null, "1234567891", "330045", null,
                "OLD SAMARITAN CORP", null, "0999999999", "330045", null,
                [new Server.PreviousOwner("0555555555", true, "OLD SAMARITAN PARENT INC", "34", "Direct owner (5% or more)", Server.OwnerRoles.Owner, 100)])]);
        var client = JsonSerializer.Deserialize<FacilityOwnership>(JsonSerializer.Serialize(server, web), web)!;
        Assert.Equal(("0446239073", "owner", 100.0, new DateTime(2025, 3, 1), "private_equity"),
            (client.Owners[0].OwnerId, client.Owners[0].RoleGroup, client.Owners[0].Percentage, client.Owners[0].Since, client.Owners[0].Kinds[0]));
        Assert.Equal("0555555555", client.Changes[0].PreviousOwners[0].OwnerId);

        var inspections = new Server.NursingHomeInspections("335001", new DateOnly(2026, 9, 30), [new Server.InspectionCount("health", 1, "J", 1, 1)],
            [new Server.InspectionSurvey("health", new DateOnly(2025, 3, 2), 1, 2, 1, "J")],
            [new Server.InspectionCitation("health", new DateOnly(2025, 3, 2), 1, "F0689", "J", "Quality of Life and Care Deficiencies", "Ensure …", "Corrected", null, true, false)],
            [new Server.NursingHomePenalty(new DateOnly(2025, 4, 1), "Fine", 182968, null, null), new Server.NursingHomePenalty(new DateOnly(2025, 4, 1), "Payment Denial", null, new DateOnly(2025, 4, 15), 30)]);
        var nh = JsonSerializer.Deserialize<NursingHomeInspections>(JsonSerializer.Serialize(inspections, web), web)!;
        Assert.Equal((182968.0, 1, "F0689", 30), (nh.FinesTotal, nh.PaymentDenials, nh.Citations[0].Tag, nh.Penalties[1].PaymentDenialDays));
    }

    [Fact]
    public async Task Owner_and_ownership_requests_are_checked_by_the_server()
    {
        await using var factory = new ApiFactory();
        using var npi = new NpiClient(factory.CreateClient());
        // Not a CCN or not an associate ID: nothing to look up (404 → null), without the database.
        Assert.Null(await npi.GetFacilityOwnershipAsync("not-a-ccn", Ct));
        Assert.Null(await npi.GetNursingHomeInspectionsAsync("x", Ct));
        Assert.Null(await npi.GetOwnerAsync("abc", Ct));

        var bad = await Assert.ThrowsAsync<NpiApiException>(() => npi.SearchOwnersAsync(kind: "pirate", pageSize: 500, cancellationToken: Ct));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(["kind", "pageSize"], bad.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Owner_search_sends_every_parameter_the_server_takes()
    {
        static string[] ServerParameters() =>
            [.. typeof(Npi.Web.Api.OwnershipEndpoints).GetMethod(nameof(Npi.Web.Api.OwnershipEndpoints.SearchOwnersAsync))!.GetParameters()
                .Where(p => p.ParameterType == typeof(string) || p.ParameterType == typeof(int?)).Select(p => p.Name!)];

        var client = typeof(NpiClient).GetMethod(nameof(NpiClient.SearchOwnersAsync))!.GetParameters()
            .Where(p => p.ParameterType == typeof(string) || p.ParameterType == typeof(int)).Select(p => p.Name!).ToArray();
        Assert.Equal(ServerParameters(), client);
    }

    [Fact]
    public async Task Unknown_provider_is_null()
    {
        await using var factory = new ApiFactory();
        using var npi = new NpiClient(factory.CreateClient());
        Assert.Null(await npi.GetProviderAsync("123", Ct));
    }

    [Fact]
    public async Task Api_key_is_sent_when_given()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string> { ["Api:RequireKey"] = "true", ["Api:Keys:0"] = "client-test-key" });

        using var anonymous = new NpiClient(factory.CreateClient());
        var denied = await Assert.ThrowsAsync<NpiApiException>(() => anonymous.GetProviderAsync("123", Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var keyed = new NpiClient(factory.CreateClient(), apiKey: "client-test-key");
        Assert.Null(await keyed.GetProviderAsync("123", Ct));
    }

    [Fact]
    public async Task Rate_limit_reports_retry_after()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string> { ["Api:PermitLimit"] = "1", ["Api:WindowSeconds"] = "3600" });
        using var npi = new NpiClient(factory.CreateClient());
        Assert.Null(await npi.GetProviderAsync("123", Ct));

        var limited = await Assert.ThrowsAsync<NpiApiException>(() => npi.GetProviderAsync("123", Ct));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task Base_address_keeps_its_path_prefix()
    {
        using var http = new HttpClient(new EchoHandler());
        using var npi = new NpiClient(http, new Uri("https://example.org/npi"));
        var ex = await Assert.ThrowsAsync<NpiApiException>(() => npi.GetMetaAsync(Ct));
        Assert.Equal("https://example.org/npi/api/v1/meta", ex.Detail);
    }

    /// <summary>Answers every request with a 500 whose body is the requested URL.</summary>
    private sealed class EchoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(request.RequestUri!.AbsoluteUri) });
    }
}
