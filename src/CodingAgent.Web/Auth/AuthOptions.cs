namespace CodingAgent.Web.Auth;

/// <summary>
/// User authentication and role-binding configuration (configuration section <c>Auth</c>).
/// In Kubernetes the chart renders the non-secret part from the <c>auth</c> Helm values into
/// <c>/app/config/auth.json</c>; the admin password and the OIDC client secret arrive as the
/// environment variables <c>Auth__Admin__Password</c> and <c>Auth__Oidc__ClientSecret</c>.
/// Validated at startup by <see cref="AuthOptionsValidator"/> (Spec 049).
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Configuration key that overrides where the rendered auth JSON file is read from.</summary>
    public const string ConfigPathKey = "Auth:ConfigPath";

    /// <summary>Path of the chart-rendered auth file inside the web pod.</summary>
    public const string DefaultConfigPath = "/app/config/auth.json";

    /// <summary>
    /// Fixed session lifetime without sliding renewal: <c>&lt;n&gt;h</c>, <c>&lt;n&gt;m</c> or a
    /// .NET TimeSpan string such as <c>1.00:00:00</c>. Parsed by <see cref="SessionDurationParser"/>.
    /// </summary>
    public string SessionDuration { get; set; } = "12h";

    /// <summary>Local admin login attempts allowed per client IP per minute.</summary>
    public int LoginRateLimitPerMinute { get; set; } = 5;

    public LocalAdminOptions Admin { get; set; } = new();

    public OidcProviderOptions Oidc { get; set; } = new();

    public RbacOptions Rbac { get; set; } = new();

    /// <summary>The parsed <see cref="SessionDuration"/>. Only valid after validation succeeded.</summary>
    public TimeSpan GetSessionDuration() =>
        SessionDurationParser.TryParse(SessionDuration, out var duration)
            ? duration
            : throw new InvalidOperationException($"Auth:SessionDuration '{SessionDuration}' is not a valid duration.");
}

/// <summary>The built-in break-glass account <c>admin</c>. Always a global admin.</summary>
public sealed class LocalAdminOptions
{
    public const string Username = "admin";

    public bool Enabled { get; set; } = true;

    /// <summary>Supplied through the <c>Auth__Admin__Password</c> environment variable (Kubernetes Secret).</summary>
    public string? Password { get; set; }
}

/// <summary>The single external OpenID Connect identity provider (Keycloak, Entra ID, ...).</summary>
public sealed class OidcProviderOptions
{
    /// <summary>Scopes requested when <see cref="Scopes"/> is empty.</summary>
    public static readonly IReadOnlyList<string> DefaultScopes = ["openid", "profile", "email"];

    public bool Enabled { get; set; }

    /// <summary>Label of the login button: "Sign in with {Name}".</summary>
    public string Name { get; set; } = "SSO";

    public string? Issuer { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Supplied through the <c>Auth__Oidc__ClientSecret</c> environment variable (Kubernetes Secret).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Requested scopes. Empty by default on purpose: the configuration binder appends to an
    /// initialised array, so defaults here would be duplicated. See <see cref="DefaultScopes"/>.
    /// </summary>
    public string[] Scopes { get; set; } = [];

    public string UsernameClaim { get; set; } = "preferred_username";

    public string GroupsClaim { get; set; } = "groups";

    public IReadOnlyList<string> EffectiveScopes => Scopes.Length > 0 ? Scopes : DefaultScopes;
}

/// <summary>Role bindings, ArgoCD style: a group or user gets a role, globally or for one project.</summary>
public sealed class RbacOptions
{
    /// <summary>Role of every authenticated user: empty (none), <c>readonly</c> or <c>operator</c>.</summary>
    public string DefaultRole { get; set; } = "";

    public List<RoleBindingOptions> Bindings { get; set; } = [];
}

/// <summary>One binding. Exactly one of <see cref="Group"/> and <see cref="User"/> is set.</summary>
public sealed class RoleBindingOptions
{
    /// <summary>Matches one value of the principal's groups claim (exact, case-sensitive).</summary>
    public string? Group { get; set; }

    /// <summary>Matches the principal's username (case-insensitive).</summary>
    public string? User { get; set; }

    /// <summary><c>readonly</c>, <c>operator</c> or <c>admin</c>.</summary>
    public string Role { get; set; } = "";

    /// <summary>Project name (exact). Empty means a global binding. Not allowed with <c>admin</c>.</summary>
    public string? Project { get; set; }
}
