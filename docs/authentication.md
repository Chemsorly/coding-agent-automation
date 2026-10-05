# Authentication and Roles

The web UI requires a signed-in user. Users sign in through one OpenID Connect identity provider (Keycloak, Microsoft Entra ID or any other OIDC provider) or with the local `admin` account. The UI has no user database: identity comes from the provider, and roles come from bindings in the Helm values, in the style of ArgoCD.

Only the web UI is affected. The Pipeline API, the Job Controller, the Scheduler and agent pods keep using the agent API keys described in [Deployment](deployment.md#agent-api-keys).

## Roles

| Role | Grants |
|---|---|
| `readonly` | View pages and runs |
| `operator` | `readonly`, plus dispatch, cancel, re-dispatch, change priority and use Agent Chat |
| `admin` | Everything, including Settings and pipeline templates |

A **binding** gives a role to an OIDC group or user, either **globally** or for **one project**. `admin` can only be bound globally: template and settings changes reach the global provider credentials.

| Action | Needs |
|---|---|
| View Overview, Work, Runs, run details, Attention, Insights, Knowledge and Pipelines for a project | `readonly` on that project |
| Open the issue, PR or epic drawers of a project's template | `readonly` on that project |
| Dispatch, cancel, re-dispatch or change the priority of a project's work | `operator` on that project |
| Agent Chat with a project selected | `operator` on that project |
| Agent Chat without a project | global `operator` |
| View Fleet, Consolidation, About statistics, health indicators and loop details | global `readonly` |
| Start, stop or resume the pipeline loop; trigger or cancel consolidation | global `operator` |
| Settings; create, edit, enable, move or delete pipeline templates | global `admin` |

Work and runs without a project count as global and need a global role.

**Project-scoped users** (users with project bindings but no global role) see one project at a time. The project switcher lists only their projects and has no "All projects" entry. Pages that span every project (Fleet, Consolidation, Settings) are hidden from them.

**Users without any role** can sign in and open their profile page (`/user`), which lists the groups the identity provider sent. An administrator binds one of those groups to a role.

## Configuration

All settings live under `auth` in the Helm values. The chart renders them, without secrets, into `/app/config/auth.json` in the web pod; secrets reach the pod as environment variables from Kubernetes Secrets.

```yaml
auth:
  sessionDuration: 12h          # fixed lifetime, no sliding renewal ("<n>h", "<n>m" or a TimeSpan)
  loginRateLimitPerMinute: 5    # local admin login attempts per client IP and web pod

  admin:
    enabled: true               # local break-glass account "admin", always a global admin
    existingSecret: ""          # empty: the chart generates <release>-coding-agent-automation-admin
    existingSecretKey: password

  oidc:
    enabled: true
    name: Keycloak              # login button: "Sign in with Keycloak"
    issuer: https://kc.example.com/realms/acme
    clientId: coding-agent
    clientSecret:
      existingSecret: coding-agent-oidc   # Secret with the client secret
      key: client-secret
    scopes: [openid, profile, email]
    usernameClaim: preferred_username
    groupsClaim: groups

  rbac:
    defaultRole: ""             # role of signed-in users without a binding: "", readonly or operator
    bindings:
      - group: platform-team
        role: admin
      - group: team-payments
        role: operator
        project: payments-api   # project name, exact match
      - user: alice@example.com
        role: readonly
```

- A `group` binding matches one value of the groups claim exactly (case-sensitive). A `user` binding matches the username claim (case-insensitive).
- A user's role is the highest of `defaultRole` and every matching binding; on a project it is the highest of the global role and the project's bindings.
- A binding to a project name that does not exist, or that two projects share, grants nothing. The web host logs a warning and the user's profile page shows the binding.
- Binding changes take effect when the web pods restart. The chart annotates the pods with a checksum of the auth configuration, so `helm upgrade` restarts them.
- Invalid configurations stop `helm template` and the web host at startup: no login method, OIDC without issuer, client ID or client secret, `admin` bound to a project, or an unknown role.

### Local admin password

When `auth.admin.existingSecret` is empty, the chart's pre-install/pre-upgrade hook creates a Secret with a random 64-character password once and keeps it across upgrades:

```bash
kubectl get secret <release>-coding-agent-automation-admin -n <namespace> \
  -o jsonpath='{.data.password}' | base64 -d
```

To supply your own password, create a Secret and set `auth.admin.existingSecret` (and `existingSecretKey` if the key is not `password`). Disable the local admin with `auth.admin.enabled: false` once OIDC works; at least one login method must stay enabled.

The login form is rate limited per client IP (`auth.loginRateLimitPerMinute`). Each web pod counts on its own, so with `web.replicas: 2` a client gets up to twice the limit per minute.

## Exposing the UI

OIDC needs the UI at the root path of its own host: an Ingress (`web.ingress`) or `kubectl port-forward` for the local admin. Serving the UI under a path prefix (`web.env.basePath`, the Rancher service proxy) is not supported once sign-in is required. Create the Ingress before you upgrade to this version.

Register this redirect URI with the identity provider:

```
https://<web host>/signin-oidc
```

The web host reads the client's scheme from `X-Forwarded-Proto`, so TLS can end at the ingress. It does not trust `X-Forwarded-Host`; the ingress must pass the original `Host` header (the default for ingress-nginx and Traefik).

With a `tls` section on the Ingress, the web host also sends clients that reached the ingress over plain HTTP (`X-Forwarded-Proto: http`) to the same URL over HTTPS, so the login form never posts a password unencrypted. Requests without `X-Forwarded-Proto` (`kubectl port-forward`, probes) are served as they are. When TLS ends in front of the ingress and the ingress itself sees only HTTP, set `web.ingress.httpsRedirect: false`; otherwise every request is redirected. HTTPS responses carry HSTS (`Strict-Transport-Security`), so a browser that once opened the UI over HTTPS keeps using HTTPS.

Every response also carries `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, `Cross-Origin-Opener-Policy`, `Cross-Origin-Resource-Policy` and a Content Security Policy with `base-uri`, `form-action`, `frame-ancestors` and `object-src`. The policy does not restrict scripts or styles, because the page shell has inline scripts.

Running several web replicas requires `signalr.redis.connectionString`: sessions are encrypted with a key ring shared through Redis. The chart refuses `web.replicas > 1` without it.

Several web replicas also need sticky sessions at the ingress. Each open page is a Blazor Server circuit that lives in one pod's memory; its connection and every reconnect must reach that pod. Without stickiness the browser's connection is routed to a pod that does not know it, and the page stays unresponsive. With Traefik, set the annotations on the web Service:

```yaml
web:
  service:
    annotations:
      traefik.ingress.kubernetes.io/service.sticky.cookie: "true"
      traefik.ingress.kubernetes.io/service.sticky.cookie.name: ca_affinity
      traefik.ingress.kubernetes.io/service.sticky.cookie.secure: "true"
      traefik.ingress.kubernetes.io/service.sticky.cookie.httponly: "true"
```

With ingress-nginx, set `nginx.ingress.kubernetes.io/affinity: cookie` in `web.ingress.annotations`.

## Keycloak

1. Create a client, for example `coding-agent`: **Client authentication** on (confidential), **Standard flow** on, everything else off.
2. **Valid redirect URIs:** `https://<web host>/signin-oidc`.
3. **Advanced → Proof Key for Code Exchange:** `S256` (recommended; the web host always sends PKCE).
4. Copy the client secret into a Kubernetes Secret and reference it in `auth.oidc.clientSecret`.
5. Add a **Group Membership** mapper to the client (or to a client scope it uses): **Token Claim Name** `groups`, **Add to ID token** on. With **Full group path** on, group values look like `/org/team-a`; with it off, `team-a`. Write bindings in the same form.
6. Set `auth.oidc.issuer` to `https://<keycloak host>/realms/<realm>`.

The web host reads claims from the ID token only; it does not call the userinfo endpoint. A mapper that adds groups only to the access token or userinfo has no effect.

## Microsoft Entra ID

1. Register an application. Add a **Web** platform with the redirect URI `https://<web host>/signin-oidc`.
2. Create a client secret and store it in a Kubernetes Secret.
3. Under **Token configuration**, add a **groups claim** for the ID token. Entra ID sends group **object IDs** (GUIDs), so bindings use the IDs: `group: 6f1c2a9e-…`.
4. Set `auth.oidc.issuer` to `https://login.microsoftonline.com/<tenant-id>/v2.0` and `usernameClaim` to `preferred_username` (or `email`).

**More than 200 groups:** Entra ID then leaves the groups out of the token and only points to Microsoft Graph. The web host does not call Graph; such a user signs in with no groups (a warning is logged). Avoid it by choosing **Groups assigned to the application** in the groups claim settings, or use **app roles** instead: define roles in the app registration, assign them to groups, and set `groupsClaim: roles`. Bindings then name the app role values.

## Sessions and sign-out

- A session lasts `auth.sessionDuration` from sign-in, without renewal. An open page re-checks its session every 5 minutes and sends the user to the login page once it has expired.
- Sessions survive web pod restarts when Redis is configured.
- **Log out** ends the session in the web UI only. The identity provider's session stays, so the next sign-in may not ask for a password. Other open tabs keep their live connection until they reconnect or the session expires.

## Troubleshooting

| Symptom | Check |
|---|---|
| "You have no access yet" after sign-in | Open the profile page (`/user`). If no groups are listed, the provider does not put groups into the ID token (Keycloak mapper, Entra groups claim or overage). If groups are listed, add a binding with one of them. |
| A binding shows "unknown project" | The `project` value must equal the project name in Settings exactly. |
| A binding shows "duplicate project name" | Two projects have that name; rename one. |
| "Sign-in with … failed" | The web host logs the reason: wrong redirect URI, client secret or issuer, or a clock skew. |
| Redirect URI uses `http://` behind TLS | The ingress must send `X-Forwarded-Proto: https`. |
| Signed out on every request with several web replicas | Configure `signalr.redis.connectionString`. |
| Pages load but stay unresponsive with several web replicas; the browser console shows "No Connection with that ID" | Enable sticky sessions at the ingress (see [Exposing the UI](#exposing-the-ui)). |
