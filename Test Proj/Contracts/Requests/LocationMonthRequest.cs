using Test_Proj.Validation;

namespace Test_Proj.Contracts.Requests;

public sealed record LocationMonthRequest(double? Latitude, double? Longitude, string? Month)
{
    public LocationMonth Validate() => LocationMonth.Create(Latitude, Longitude, Month);
}
