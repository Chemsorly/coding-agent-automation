using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="CockpitState"/> — the circuit-scoped cockpit shell state. Verifies the
/// event contract that keeps project-scoped pages from looping: <see cref="CockpitState.OnChange"/>
/// fires on any change, while <see cref="CockpitState.OnProjectChanged"/> fires only on a project change.
/// </summary>
public class CockpitStateTests
{
    [Fact]
    public void SetProject_ChangesState_AndRaisesBothEvents()
    {
        var state = new CockpitState();
        int onChange = 0, onProject = 0;
        state.OnChange += () => onChange++;
        state.OnProjectChanged += () => onProject++;

        state.SetProject("p1", "Project One");

        Assert.Equal("p1", state.SelectedProjectId);
        Assert.Equal("Project One", state.SelectedProjectName);
        Assert.Equal(1, onChange);
        Assert.Equal(1, onProject);
    }

    [Fact]
    public void SetProject_SameId_IsNoOp_AndRaisesNoEvents()
    {
        var state = new CockpitState();
        state.SetProject("p1", "One");

        int onChange = 0, onProject = 0;
        state.OnChange += () => onChange++;
        state.OnProjectChanged += () => onProject++;

        state.SetProject("p1", "One again");

        Assert.Equal("One", state.SelectedProjectName); // unchanged — early return before assignment
        Assert.Equal(0, onChange);
        Assert.Equal(0, onProject);
    }

    [Fact]
    public void SetProject_NullId_NormalizesToEmpty()
    {
        var state = new CockpitState();
        state.SetProject("p1", "One");

        state.SetProject(null, null);

        Assert.Equal("", state.SelectedProjectId);
        Assert.Null(state.SelectedProjectName);
    }

    [Fact]
    public void SetAttentionCount_ChangesCount_RaisesOnChangeOnly()
    {
        var state = new CockpitState();
        int onChange = 0, onProject = 0;
        state.OnChange += () => onChange++;
        state.OnProjectChanged += () => onProject++;

        state.SetAttentionCount(3);

        Assert.Equal(3, state.AttentionCount);
        Assert.Equal(1, onChange);
        Assert.Equal(0, onProject); // deliberately NOT raised, so pages that re-query on project change don't loop
    }

    [Fact]
    public void SetAttentionCount_SameCount_IsNoOp()
    {
        var state = new CockpitState();
        state.SetAttentionCount(3);

        int onChange = 0;
        state.OnChange += () => onChange++;

        state.SetAttentionCount(3);

        Assert.Equal(0, onChange);
    }
}
