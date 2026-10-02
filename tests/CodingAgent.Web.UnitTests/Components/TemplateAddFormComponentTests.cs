using Bunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for TemplateAddForm, which adds a template or, opened from a template's Edit button, edits one in place.
/// </summary>
public class TemplateAddFormComponentTests : BunitContext
{
    private IRenderedComponent<TemplateAddForm> RenderForm(TemplateTableSection.TemplateFormModel form) =>
        Render<TemplateAddForm>(p => p
            .Add(s => s.Form, form)
            .Add(s => s.Projects, new List<PipelineProject> { new() { Id = "proj", Name = "Product" } })
            .Add(s => s.IssueProviders, new List<ProviderConfig>())
            .Add(s => s.RepoProviders, new List<ProviderConfig>())
            .Add(s => s.BrainProviders, new List<ProviderConfig>())
            .Add(s => s.PipelineProviders, new List<ProviderConfig>()));

    [Fact]
    public void AddMode_OffersProjectAndBindings()
    {
        var cut = RenderForm(new TemplateTableSection.TemplateFormModel());

        Assert.Contains("Add Pipeline Job Template", cut.Markup);
        var selects = cut.FindAll("select");
        Assert.Contains(selects, s => s.OuterHtml.Contains("Product"));
        Assert.All(selects, s => Assert.False(s.HasAttribute("disabled")));
    }

    [Fact]
    public void EditMode_LocksTheTrackerAndRepository_AndLeavesTheProjectToMove()
    {
        var template = new PipelineJobTemplate { Id = "t-1", Name = "Api", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

        var cut = RenderForm(TemplateTableSection.TemplateFormModel.ForEdit(template, "proj"));

        Assert.Contains("Edit Pipeline Job Template", cut.Markup);
        Assert.Contains("add a new template", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("select"), s => s.OuterHtml.Contains("Product"));
        var locked = cut.FindAll("select").Where(s => s.HasAttribute("disabled")).ToList();
        Assert.Equal(2, locked.Count);
        Assert.Equal("Api", cut.Find("input[type='text']").GetAttribute("value"));
    }
}
