using Bunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for TemplateAddForm, which adds a new pipeline job template.
/// Editing an existing template happens on its repository page (/pipelines/{id}).
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
}
