using Test_Proj.Validation;

namespace Test_Proj.Export;

/// <summary>Names and schema are code-owned. No path or header input is accepted.</summary>
public sealed class ExportFile
{
    public string Filename { get; }
    public IReadOnlyList<string> Headers { get; }
    private ExportFile(string filename, params string[] headers) =>
        (Filename, Headers) = (filename, Array.AsReadOnly(headers));

    public static ExportFile Forces { get; } = new("Forces.csv", "id", "name");
    public static ExportFile Crimes(LocationMonth request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new($"Crimes_{request.Month}.csv", "id", "persistent_id", "category", "month", "latitude",
            "longitude", "street_id", "street_name", "location_type", "context", "outcome_category", "outcome_date");
    }
    public static ExportFile StopSearches(LocationMonth request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new($"StopSearches_{request.Month}.csv", "type", "datetime", "age_range", "gender",
            "self_defined_ethnicity", "officer_defined_ethnicity", "legislation", "object_of_search", "outcome",
            "involved_person", "operation", "operation_name", "latitude", "longitude", "street_id", "street_name");
    }
}
