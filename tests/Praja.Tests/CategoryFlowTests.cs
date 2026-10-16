using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Xunit;
namespace Praja.Tests;

public class CategoryFlowTests
{
    [Fact]
    public async Task DraftPersistsHierarchyAndEvidenceForOfficerReview()
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        var created=await Post(client,"/Grievances/New",Fields());Assert.Equal(HttpStatusCode.Redirect,created.StatusCode);
        using var scope=app.Services.CreateScope();var item=await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync();
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(item));
        Assert.True(json.RootElement.TryGetProperty("CategoryCode",out var category));Assert.Equal("healthcare_access",category.GetString());
        Assert.Equal("medicine_shortage",json.RootElement.GetProperty("SubcategoryCode").GetString());
        Assert.Contains(json.RootElement.GetProperty("RoutingEvidence").GetString()!,item.OriginalText);
        var html=await client.GetStringAsync(created.Headers.Location!);Assert.Contains("Demo category",html);Assert.Contains("Medicine availability",html);Assert.Equal("Draft",item.Status);
    }
    [Fact]
    public async Task MismatchedCategoryCannotBeConfirmedUnderAnotherService()
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        var created=await Post(client,"/Grievances/New",Fields());var path=created.Headers.Location!.ToString();
        var response=await Post(client,path+"?handler=Register",new(){["Version"]="1",["Title"]="Hospital medicine shortage",["DraftText"]=Fields()["Input.OriginalText"],["Department"]="roads",["CategoryCode"]="healthcare_access",["SubcategoryCode"]="medicine_shortage",["Reviewed"]="true"});
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var scope=app.Services.CreateScope();Assert.Equal("Draft",(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync()).Status);
        Assert.Contains("Hospital medicine shortage",await response.Content.ReadAsStringAsync());
    }
    [Theory]
    [InlineData("","")]
    [InlineData("healthcare_access","")]
    public async Task OfficerCanExplicitlyClearSuggestedCategoryOrIssue(string category,string issue)
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        var created=await Post(client,"/Grievances/New",Fields());var path=created.Headers.Location!.ToString();
        var response=await Post(client,path+"?handler=Register",new(){["Version"]="1",["Title"]="Hospital medicine shortage",["DraftText"]=Fields()["Input.OriginalText"],["Department"]="health",["CategoryCode"]=category,["SubcategoryCode"]=issue,["Reviewed"]="true"});
        Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);using var scope=app.Services.CreateScope();var saved=await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Grievances.SingleAsync();Assert.Equal(category,saved.CategoryCode);Assert.Equal(issue,saved.SubcategoryCode);
    }
    [Fact]
    public async Task RecoverableRoutingErrorKeepsOfficerDraftEditsForCorrection()
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);
        var created=await Post(client,"/Grievances/New",Fields());var path=created.Headers.Location!.ToString();
        var edited="Officer corrected complaint wording after checking the original and citizen clarification.";
        var response=await Post(client,path+"?handler=Register",new(){["Version"]="1",["Title"]="Officer revised title",["DraftText"]=edited,["Department"]="roads",["CategoryCode"]="healthcare_access",["SubcategoryCode"]="medicine_shortage",["Reviewed"]="true"});
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);var html=await response.Content.ReadAsStringAsync();Assert.Contains(edited,html);Assert.Contains("Officer revised title",html);Assert.DoesNotContain("checked=\"checked\"",html);
    }
    private static Dictionary<string,string> Fields()=>new(){["Input.OriginalText"]="No medicines are available at the government hospital. Please restore medicine stock.",["Input.District"]="Mysuru",["Input.Location"]="Ward 12",["Input.Language"]="English",["Input.Source"]="Text",["Input.Consent"]="true"};
    private static async Task Setup(HttpClient client)=>Assert.Equal(HttpStatusCode.Redirect,(await Post(client,"/Setup",new(){["Input.Name"]="Category reviewer",["Input.Email"]="categories@example.test",["Input.Password"]="Categories-Test-Password-72!"})).StatusCode);
    private static async Task<HttpResponseMessage> Post(HttpClient client,string path,Dictionary<string,string> fields){var html=await client.GetStringAsync(path);fields["__RequestVerificationToken"]=WebUtility.HtmlDecode(Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);return await client.PostAsync(path,new FormUrlEncodedContent(fields));}
}
