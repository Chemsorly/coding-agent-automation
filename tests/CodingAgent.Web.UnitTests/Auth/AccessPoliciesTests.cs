using AwesomeAssertions;
using CodingAgent.Web.Auth;

namespace CodingAgent.Web.UnitTests.Auth;

/// <summary>Spec 049 Req 6.1/7.1: which grants satisfy which page policy.</summary>
public class AccessPoliciesTests
{
    private const string ProjectP = "6f1c2a9e-0000-0000-0000-00000000000a";

    public static TheoryData<string, AccessGrant, bool, bool, bool, bool> Matrix => new()
    {
        // name, grant, AnyAccess, AnyOperator, GlobalRead, Admin
        { "none", AccessGrant.None, false, false, false, false },
        { "global readonly", TestAccess.Global(AccessRole.ReadOnly), true, false, true, false },
        { "global operator", TestAccess.Global(AccessRole.Operator), true, true, true, false },
        { "global admin", TestAccess.Global(AccessRole.Admin), true, true, true, true },
        { "readonly on P", TestAccess.Scoped((ProjectP, AccessRole.ReadOnly)), true, false, false, false },
        { "operator on P", TestAccess.Scoped((ProjectP, AccessRole.Operator)), true, true, false, false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void PolicyMatrix(string name, AccessGrant grant, bool anyAccess, bool anyOperator, bool globalRead, bool admin)
    {
        AccessAuthorizationHandler.IsSatisfied(grant, AccessPolicyKind.AnyAccess).Should().Be(anyAccess, name);
        AccessAuthorizationHandler.IsSatisfied(grant, AccessPolicyKind.AnyOperator).Should().Be(anyOperator, name);
        AccessAuthorizationHandler.IsSatisfied(grant, AccessPolicyKind.GlobalRead).Should().Be(globalRead, name);
        AccessAuthorizationHandler.IsSatisfied(grant, AccessPolicyKind.Admin).Should().Be(admin, name);
    }
}
