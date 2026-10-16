using Bunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for the new HideLabels and SecretsWriteOnly parameters added to RepositorySettingsEditor.
/// </summary>
public class RepositorySettingsEditorParameterTests : BunitContext
{
    private IRenderedComponent<RepositorySettingsEditor> Render(
        TemplateTableSection.TemplateFormModel form,
        bool hideLabels = false,
        bool secretsWriteOnly = false)
    {
        var cut = Render<RepositorySettingsEditor>(p => p
            .Add(s => s.Form, form)
            .Add(s => s.HideLabels, hideLabels)
            .Add(s => s.SecretsWriteOnly, secretsWriteOnly));
        return cut;
    }

    [Fact]
    public void HideLabels_SuppressesLabelsInput()
    {
        var form = new TemplateTableSection.TemplateFormModel
        {
            RequiredLabels = ["kiro", "dotnet"]
        };

        var cut = Render(form, hideLabels: true);

        // "Required Agent Labels" input must not appear
        Assert.DoesNotContain("Required Agent Labels", cut.Markup);
    }

    [Fact]
    public void HideLabels_False_ShowsLabelsInput()
    {
        var cut = Render(new TemplateTableSection.TemplateFormModel());

        // Default: labels input is rendered
        Assert.Contains("Required Agent Labels", cut.Markup);
    }

    [Fact]
    public void SecretsWriteOnly_DoesNotRenderStoredValues()
    {
        var form = new TemplateTableSection.TemplateFormModel
        {
            Secrets = new Dictionary<string, string> { ["API_KEY"] = "value123_secret" }
        };

        var cut = Render(form, secretsWriteOnly: true);

        // Expand the secrets section
        cut.Find(".secrets-section-header").Click();

        // The stored secret value must NEVER appear in markup
        Assert.DoesNotContain("value123_secret", cut.Markup);
    }

    [Fact]
    public void SecretsWriteOnly_ShowsKeyNamesAndReplaceButton()
    {
        var form = new TemplateTableSection.TemplateFormModel
        {
            Secrets = new Dictionary<string, string>
            {
                ["KEY_ONE"] = "secret1",
                ["KEY_TWO"] = "secret2"
            }
        };

        var cut = Render(form, secretsWriteOnly: true);
        cut.Find(".secrets-section-header").Click();

        // Key names are visible
        Assert.Contains("KEY_ONE", cut.Markup);
        Assert.Contains("KEY_TWO", cut.Markup);

        // "Replace" buttons are present (one per secret)
        var replaceButtons = cut.FindAll("button[title='Replace']");
        Assert.Equal(2, replaceButtons.Count);

        // No password inputs (the stored value is never bound to an input)
        Assert.Empty(cut.FindAll("input[type='password']"));
    }

    [Fact]
    public async Task SecretsWriteOnly_ReplaceInput_AppearsAfterClickAndDoesNotShowStoredValue()
    {
        var form = new TemplateTableSection.TemplateFormModel
        {
            Secrets = new Dictionary<string, string> { ["DB_PASS"] = "old-secret-xyz" }
        };

        var cut = Render(form, secretsWriteOnly: true);
        cut.Find(".secrets-section-header").Click();

        // Initially: Replace button visible, no text input shown, stored value absent
        Assert.NotEmpty(cut.FindAll("button[title='Replace']"));
        Assert.Empty(cut.FindAll("input.secret-value-input"));
        Assert.DoesNotContain("old-secret-xyz", cut.Markup);

        // Click Replace for the first secret
        await cut.InvokeAsync(() => cut.Find("button[title='Replace']").Click());

        // After click: Replace input appears (empty, not pre-filled with stored value)
        var replaceInput = cut.Find("input.secret-value-input");
        Assert.NotNull(replaceInput);
        var inputValue = replaceInput.GetAttribute("value") ?? "";
        Assert.DoesNotContain("old-secret-xyz", inputValue);

        // The stored value must never appear anywhere in markup
        Assert.DoesNotContain("old-secret-xyz", cut.Markup);

        // Cancel button appears, allows dismissing replace
        var cancelBtn = cut.Find("button[title='Cancel replace']");
        Assert.NotNull(cancelBtn);
    }

    [Fact]
    public void SecretsNotWriteOnly_ShowsPasswordInputsWithReveal()
    {
        var form = new TemplateTableSection.TemplateFormModel
        {
            Secrets = new Dictionary<string, string> { ["TOKEN"] = "my-token" }
        };

        var cut = Render(form, secretsWriteOnly: false);
        cut.Find(".secrets-section-header").Click();

        // Standard mode: password input exists with reveal button
        Assert.NotEmpty(cut.FindAll("input[type='password']"));
        Assert.NotEmpty(cut.FindAll("button[title='Reveal']"));
        // No "Replace" buttons in standard mode
        Assert.Empty(cut.FindAll("button[title='Replace']"));
        // NOTE: This test does not verify that the stored value "my-token" is pre-populated in
        // the password input. A regression that also hides stored values in non-write-only mode
        // would not be detected. Add an assertion that the input's value equals "my-token" after
        // clicking Reveal (switching to input[type='text']). (TestQualityReviewer finding, issue #3576)
    }
}
