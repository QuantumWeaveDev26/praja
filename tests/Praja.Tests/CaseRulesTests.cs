using Praja.Web.Domain;
using Xunit;

namespace Praja.Tests;

public class CaseRulesTests
{
    [Theory]
    [InlineData("The drinking water supply has stopped for five days.", "water")]
    [InlineData("ರಸ್ತೆಯಲ್ಲಿ ದೊಡ್ಡ ಗುಂಡಿಗಳಿವೆ ಮತ್ತು ಅಪಘಾತವಾಗುತ್ತಿದೆ", "roads")]
    public void RoutesKnownComplaintTerms(string complaint, string expected)
    {
        Assert.Equal(expected, CaseRules.SuggestDepartment(complaint).Department);
    }

    [Theory]
    [InlineData("Please help with my pending application.")]
    [InlineData("There is no water and the road is damaged.")]
    public void AmbiguousRoutingRequiresHumanChoice(string complaint)
    {
        var result = CaseRules.SuggestDepartment(complaint);
        Assert.Equal("review", result.Department);
        Assert.True(result.NeedsManualReview);
    }

    [Fact]
    public void CitizenDisagreementPreventsClosure()
    {
        var result = CaseRules.AssessClosure("No drinking water in Ward 12", new string('a', 120), "Inspection record WR-12", "Disagreed");
        Assert.False(result.CanClose);
        Assert.Contains(result.Flags, flag => flag.Contains("citizen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingEvidencePreventsClosure()
    {
        var result = CaseRules.AssessClosure("No drinking water", "Water supply was restored after replacing the damaged valve. Ward 12 inspection completed on site and supply checked.", "", "Confirmed");
        Assert.False(result.CanClose);
        Assert.Contains(result.Flags, flag => flag.Contains("evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DocumentChecklistDoesNotProvePhysicalResolution()
    {
        var result = CaseRules.AssessClosure("No drinking water", "Water supply was restored after replacing the damaged valve. Ward 12 inspection completed on site and supply checked.", "Inspection WR-12", "Confirmed");
        Assert.True(result.CanClose);
        Assert.Throws<InvalidOperationException>(() => CaseRules.ValidateTransition("ClosureReview", "Closed", false, result));
    }

    [Fact]
    public void CannotSkipOfficerWorkAndCloseDraft()
    {
        Assert.Throws<InvalidOperationException>(() => CaseRules.ValidateTransition("Draft", "Closed", true, new ClosureAssessment([])));
    }
}
