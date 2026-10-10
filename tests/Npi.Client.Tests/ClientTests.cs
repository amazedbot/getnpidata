using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
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
        Classification = "Chiropractor", Specialization = "Sports Physician", Taxonomy = "111NS0005X", State = "NY", County = "36103",
        City = "Babylon", Zip = "11702", Radius = 10, LastName = "o'brien & co", FirstName = "jose", OrgName = "acme", NameMatch = "similar", Npi = "1234567893",
        EntityType = 1, Gender = "F", Credential = "MD", Excluded = false, OptedOut = true, OrderRefer = true, AcceptsAssignment = true, Telehealth = false,
        MinYears = 10, MedicareActive = true, Shortage = "dental", NewWithinDays = 30, UpdatedWithinDays = 7, Sort = "-city", Page = 3, PageSize = 100,
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
            Classification = "Chiropractor", Specialization = "Sports Physician", TaxonomyCode = "111NS0005X", State = "NY", CountyFips = "36103",
            City = "Babylon", Zip5 = "11702", RadiusMiles = 10, LastName = "o'brien & co", FirstName = "jose", OrgName = "acme", NameMatch = "similar", Npi = "1234567893",
            EntityType = 1, Gender = "F", Credential = "MD", Excluded = false, OptedOut = true, OrderRefer = true, AcceptsAssignment = true,
            Telehealth = false, MinYears = 10, MedicareActive = true, Shortage = "dental", NewWithinDays = 30, UpdatedWithinDays = 7, Sort = "-city", Page = 3, PageSize = 100,
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
    [InlineData(typeof(Server.LookupRow), typeof(LookupRow))]
    [InlineData(typeof(Npi.Web.Api.LookupResponse), typeof(LookupResponse))]
    public void Client_models_have_every_field_the_server_sends(Type server, Type client)
    {
        static IEnumerable<string> Names(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract").Select(p => p.Name);

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
    public async Task Invalid_search_throws_with_errors_by_parameter_name()
    {
        await using var factory = new ApiFactory();
        using var npi = new NpiClient(factory.CreateClient());

        var none = await Assert.ThrowsAsync<NpiApiException>(() => npi.SearchProvidersAsync(new ProviderSearch(), Ct));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.True(none.Errors.ContainsKey("filter"));

        var radius = await Assert.ThrowsAsync<NpiApiException>(() => npi.SearchProvidersAsync(new ProviderSearch { Zip = "11701", Radius = 500 }, Ct));
        Assert.Contains("radius", radius.Errors.Keys);
        Assert.Contains("[radius]", radius.Message, StringComparison.Ordinal);

        using var csv = new MemoryStream();
        var badCsv = await Assert.ThrowsAsync<NpiApiException>(() => npi.DownloadProvidersCsvAsync(new ProviderSearch { Zip = "117" }, csv, Ct));
        Assert.Contains("zip", badCsv.Errors.Keys);
        Assert.Equal(0, csv.Length);
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
