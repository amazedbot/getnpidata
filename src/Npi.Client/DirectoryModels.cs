namespace Npi.Client;

/// <summary>A page of Medicare-certified facilities (/api/v1/facilities).</summary>
public sealed class FacilityPage
{
    public IReadOnlyList<FacilitySummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }
}

/// <summary>A Medicare-certified facility: from CMS's Provider of Services file and Medicare Care Compare, by its CCN.</summary>
public sealed class FacilitySummary
{
    /// <summary>CMS Certification Number (6 characters; 10 for surgery centers).</summary>
    public string Ccn { get; set; } = "";

    /// <summary>hospital, nursing_home, home_health, hospice, fqhc, rhc, dialysis, asc or other.</summary>
    public string Type { get; set; } = "";

    /// <summary>"Hospital", "Rural health clinic" ….</summary>
    public string TypeName { get; set; } = "";

    /// <summary>A finer kind when known: "Critical access hospital", "Community mental health center" ….</summary>
    public string? Kind { get; set; }

    public string Name { get; set; } = "";

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Zip { get; set; }

    public string? Phone { get; set; }

    public string? Ownership { get; set; }

    /// <summary>Certified beds (nursing homes).</summary>
    public int? Beds { get; set; }

    /// <summary>The type's CMS star rating, 1–5 (home health in half stars); null when not rated.</summary>
    public double? Rating { get; set; }

    /// <summary>False: its Medicare certification ended on <see cref="TerminationDate"/>.</summary>
    public bool Active { get; set; }

    public DateTime? TerminationDate { get; set; }

    public string? TerminationReason { get; set; }

    /// <summary>Active NPIs CMS's enrollment files link to the facility.</summary>
    public int BillingNpis { get; set; }

    /// <summary>The centre of the facility's ZIP code (approximate).</summary>
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>The newest change of ownership CMS recorded for the facility as buyer or seller (hospitals and nursing homes, since 2016).</summary>
    public DateTime? LastOwnershipChange { get; set; }

    /// <summary>A nursing home's fines and payment denials in Care Compare's penalty file (the last three years).</summary>
    public int Penalties { get; set; }

    /// <summary>The total of those fines, in dollars.</summary>
    public double? Fines { get; set; }

    public DateTime? LastPenalty { get; set; }
}

/// <summary>A page of Medicare group practices (/api/v1/groups).</summary>
public sealed class GroupPage
{
    public IReadOnlyList<GroupSummary> Items { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }
}

/// <summary>A Medicare group practice, by its PAC ID.</summary>
public sealed class GroupSummary
{
    /// <summary>PECOS associate control ID (10 digits).</summary>
    public string PacId { get; set; } = "";

    public string Name { get; set; } = "";

    public string? State { get; set; }

    /// <summary>Active clinicians reassigned to the group in Medicare enrollment or listed with it in Care Compare.</summary>
    public int Clinicians { get; set; }

    /// <summary>Active organization NPIs enrolled in Medicare under the group's ID.</summary>
    public int OrganizationNpis { get; set; }

    public bool? AcceptsMedicare { get; set; }

    /// <summary>The group's MIPS final score (0–100) in <see cref="MipsYear"/>, when it reported as a group.</summary>
    public double? MipsScore { get; set; }

    public int? MipsYear { get; set; }
}
