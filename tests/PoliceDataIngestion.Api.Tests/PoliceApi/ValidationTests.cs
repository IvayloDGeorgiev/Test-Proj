using System.Globalization;
using Test_Proj.Validation;

namespace PoliceDataIngestion.Api.Tests.PoliceApi;

public sealed class ValidationTests
{
    [Theory]
    [InlineData(-90, -180, "0001-01")]
    [InlineData(90, 180, "9999-12")]
    [InlineData(0d, 0, "2024-02")]
    [InlineData(-0.0, -0.0, "2026-09")]
    public void Create_ValidBoundaries_PreservesValues(double latitude, double longitude, string month)
    {
        // Arrange / Act
        var result = LocationMonth.Create(latitude, longitude, month);
        // Assert
        Assert.Equal(latitude, result.Latitude);
        Assert.Equal(longitude, result.Longitude);
        Assert.Equal(month, result.Month);
    }

    [Theory]
    [InlineData(null, 0d)] [InlineData(0d, null)]
    [InlineData(-90.000001, 0d)] [InlineData(90.000001, 0d)]
    [InlineData(0d, -180.000001)] [InlineData(0d, 180.000001)]
    [InlineData(double.NaN, 0d)] [InlineData(0d, double.NaN)]
    [InlineData(double.PositiveInfinity, 0d)] [InlineData(double.NegativeInfinity, 0d)]
    [InlineData(0d, double.PositiveInfinity)] [InlineData(0d, double.NegativeInfinity)]
    public async Task Create_InvalidCoordinates_RejectsBeforeHttp(double? latitude, double? longitude)
    {
        // Arrange
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response()));
        // Act
        var exception = await Assert.ThrowsAsync<RequestValidationException>(async () =>
        {
            LocationMonth.Create(latitude, longitude, "2026-01");
            await fixture.Client.GetForcesAsync();
        });
        // Assert
        Assert.NotEmpty(exception.Errors);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("2026-1")] [InlineData("2026-00")]
    [InlineData("2026-13")] [InlineData("0000-01")] [InlineData("10000-01")]
    [InlineData("٢٠٢٦-01")] [InlineData("2026-０１")] [InlineData("2026-01\n")]
    [InlineData(" 2026-01")] [InlineData("2026/01")] [InlineData("2026-01-01")]
    public void Create_InvalidMonth_ReturnsSafeFieldError(string? month)
    {
        // Arrange / Act
        var exception = Assert.Throws<RequestValidationException>(() => LocationMonth.Create(0, 0, month));
        // Assert
        Assert.Equal("month", Assert.Single(exception.Errors).Key);
        Assert.Equal("Request validation failed.", exception.Message);
    }

    [Fact]
    public void ToQueryString_NonEnglishCulture_UsesInvariantNumbers()
    {
        // Arrange
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var location = LocationMonth.Create(53.8008, -1.5491, "2026-09");
            // Act
            var query = location.ToQueryString();
            // Assert
            Assert.Equal("lat=53.8008&lng=-1.5491&date=2026-09", query);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
