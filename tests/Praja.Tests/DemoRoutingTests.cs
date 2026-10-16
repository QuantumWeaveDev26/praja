using System.Text.Json;
using Praja.Web.Domain;
using Xunit;

namespace Praja.Tests;

public class DemoRoutingTests
{
    [Theory]
    [InlineData("No water supply in Ward 12 for three days.", "water", "water_supply", "no_supply")]
    [InlineData("There are potholes on the road near the market.", "roads", "road_condition", "potholes")]
    [InlineData("The electricity supply has low voltage every evening.", "electricity", "power_supply", "low_voltage")]
    [InlineData("ಕಸ ಸಂಗ್ರಹವಾಗಿಲ್ಲ. ದಯವಿಟ್ಟು ಕಸ ತೆಗೆದುಹಾಕಿ.", "sanitation", "waste_collection", "uncollected_waste")]
    [InlineData("No medicines are available at the government hospital.", "health", "healthcare_access", "medicine_shortage")]
    [InlineData("ಆಸ್ಪತ್ರೆಯಲ್ಲಿ ಔಷಧಿ ಇಲ್ಲ. ದಯವಿಟ್ಟು ಸಹಾಯ ಮಾಡಿ.", "health", "healthcare_access", "medicine_shortage")]
    [InlineData("There are no teachers at the government school.", "education", "school_facilities", "teacher_shortage")]
    [InlineData("ಶಾಲೆಯಲ್ಲಿ ಶಿಕ್ಷಕರಿಲ್ಲ. ದಯವಿಟ್ಟು ಶಿಕ್ಷಕರನ್ನು ನೇಮಿಸಿ.", "education", "school_facilities", "teacher_shortage")]
    [InlineData("The scheduled bus did not arrive at the village.", "transport", "bus_services", "missed_bus")]
    [InlineData("ಬೆಳೆ ಪರಿಹಾರ ಇನ್ನೂ ಬಂದಿಲ್ಲ. ಕೃಷಿ ಇಲಾಖೆಯಿಂದ ಸಹಾಯ ಬೇಕು.", "agriculture", "farm_support", "crop_assistance")]
    public void BilingualFixturesProduceReviewableDemoHierarchy(string original, string department, string category, string issue)
    {
        var route = CaseRules.SuggestDepartment(original); Assert.Equal(department, route.Department);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(route));
        Assert.True(json.RootElement.TryGetProperty("Category", out var selectedCategory)); Assert.Equal(category, selectedCategory.GetString());
        Assert.Equal(issue, json.RootElement.GetProperty("Subcategory").GetString());
        var evidence = json.RootElement.GetProperty("Evidence").GetString(); Assert.False(string.IsNullOrEmpty(evidence)); Assert.Contains(evidence!, original);
    }
    [Theory]
    [InlineData("ನೀರು ಇಲ್ಲ and the road has potholes.")]
    [InlineData("No medicines in the hospital and no teachers at the school.")]
    [InlineData("My broadband connection has failed.")]
    [InlineData("Please help with this pending application.")]
    public void MixedUnknownAndSubstringMatchesRemainForManualReview(string original)
    {
        var route = CaseRules.SuggestDepartment(original); Assert.Equal("review", route.Department); Assert.True(route.NeedsManualReview);
    }
}
