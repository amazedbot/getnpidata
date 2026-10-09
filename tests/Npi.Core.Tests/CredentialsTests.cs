using Npi.Core.Search;

namespace Npi.Core.Tests;

public class CredentialsTests
{
    // What the loader learns from the data: key → standard credential(s); MSPT is two credentials run together.
    private static readonly Dictionary<string, IReadOnlyList<string>> Known = new()
    {
        ["MD"] = ["MD"], ["PHD"] = ["PhD"], ["MS"] = ["MS"], ["PT"] = ["PT"], ["DPT"] = ["DPT"], ["CCCSLP"] = ["CCC-SLP"], ["PAC"] = ["PA-C"],
        ["PHARMD"] = ["PharmD"], ["OTRL"] = ["OTR/L"], ["MSPT"] = ["MS", "PT"], ["RN"] = ["RN"], ["BSN"] = ["BSN"], ["CASEMANAGER"] = ["CASEMANAGER"],
    };

    private static IReadOnlyList<string> Standardize(string raw) => Credentials.Standardize(raw, k => Known.GetValueOrDefault(k));

    [Theory]
    [InlineData("M.D.", "MD")]
    [InlineData("M. D.", "MD")]
    [InlineData("m.d", "MD")]
    [InlineData("MD, PH.D.", "MD|PhD")]
    [InlineData("MD/PHD", "MD|PhD")]
    [InlineData("MD PHD", "MD|PhD")]
    [InlineData("PT, DPT", "PT|DPT")]
    [InlineData("M.S. CCC-SLP", "MS|CCC-SLP")]
    [InlineData("MS CCC SLP", "MS|CCC-SLP")]           // a credential written with spaces
    [InlineData("MS, CCC/SLP", "MS|CCC-SLP")]          // … or split at a slash
    [InlineData("M.S.P.T.", "MS|PT")]                  // two credentials run together
    [InlineData("PHARM. D.", "PharmD")]
    [InlineData("PA C", "PA-C")]
    [InlineData("OTR/L", "OTR/L")]                     // a slash before one letter is part of the credential
    [InlineData("RN, BSN, RN", "RN|BSN")]              // duplicates dropped
    [InlineData("NURSE PRACTITIONER", "NP")]           // a spelled-out title
    [InlineData("DOCTOR OF CHIROPRACTIC", "DC")]
    [InlineData("CASE MANAGER", "CASE MANAGER")]       // a long phrase keeps its words
    [InlineData("LPC ASSOCIATE", "LPC ASSOCIATE")]     // a word that is no credential: kept as written
    [InlineData("N/A", "")]
    [InlineData("NONE", "")]
    [InlineData("", "")]
    public void Raw_credentials_are_standardized(string raw, string expected) =>
        Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), Standardize(raw));

    [Fact]
    public void Keys_ignore_punctuation_and_case() =>
        Assert.Equal(["MD", "PAC", "OTRL"], new[] { "m.d.", "PA-C", "OTR/L" }.Select(Credentials.Key));
}
