# Authentication and Roles

The web UI requires a signed-in user. Users sign in through one OpenID Connect identity provider (Keycloak, Microsoft Entra ID or any other OIDC provider) or with the local `admin` account. The UI has no user database: identity comes from the provider, and roles come from bindings in the Helm values, in the style of ArgoCD.

The Job Controller, the Scheduler and agent pods use the agent API keys described in [Deployment](deployment.md#agent-api-keys).

The Pipeline API has two surfaces:

- **Service surface** (`/api/**` except `/api/ui/**`, and `/hubs/agent`): API keys only.
- **UI surface** (`/api/ui/**` and `/hubs/ui`): a short-lived **user token** only. The web host signs it for the signed-in user (HS256, `iss=caa-web`, `aud=caa-api`, at most 5 minutes for HTTP calls; the UI hub's connect token lasts the session, is marked `use=hub` and is accepted only on `/hubs/ui`, and the hub closes the connection when it expires). It carries the user's global role and project roles, and the API enforces them itself. The signing key is the `signing-key` of the `<release>-web-user-token` Secret ([Deployment](deployment.md#user-token-secret)), mounted into the web and API pods only. Both hosts refuse to start without it. The web host mints a token for each request it forwards (one per hub connection), and only for users with a role. Tokens never reach the browser, and the web host never calls the UI surface with the master API key.

An API endpoint that declares no policy is denied. A credential of one surface is never accepted on the other.

## The web host and the browser client

The UI runs as Blazor WebAssembly in the browser. The web host is its backend-for-frontend (BFF) and the only internet-facing process. It does exactly this:

- signs users in (login page, local admin, OIDC), keeps the session cookie, logs out and rate-limits the login form;
- evaluates the role bindings into the user's grant;
- answers `GET /bff/session`: who is signed in, when the session ends, the evaluated grant with the matched bindings, and the OIDC provider's name (build details only for a user with a role). The browser shows what this says; it never evaluates bindings itself;
- serves the host page and the client's files;
- forwards `/api/ui/**` and `/hubs/ui` to the Pipeline API, and nothing else, with a user token in place of the cookie;
- answers `/healthz` and `/readyz`.

It renders no pages, keeps no page state and has no API of its own. The web pod has no service-account token, database credentials, Redis access, master or agent API key, or provider credentials. It holds only the `web-user-token` Secret, the OIDC client secret, the local admin password and, when set, the OTLP exporter headers (`otel-headers` of the chart Secret). When the web host is down, only the UI is affected: the Scheduler, the Job Controller, the API and the agents keep running.

**Forwarding.** The browser's only credential is the session cookie (`SameSite=Lax`). On a forwarded request the web host removes the cookie, any `Authorization` or `X-Api-Key` header and the CSRF header, and adds the user token. It removes `Set-Cookie` from the API's answers and marks them `Cache-Control: no-store` unless the API set a cache policy. It answers `502` when the API cannot be reached and `504` after 5 minutes without activity.

**Request checks.** They run before sign-in on every request to `/api/**`, `/hubs/**` and `/bff/**`, in this order:

1. An encoded slash or backslash (`%2F`, `%5C`) in the path is `400`. An ID that contains `/` (possible only through a config import) therefore cannot be used through the UI.
2. `/hubs/ui` without a WebSocket upgrade is `400`; an upgrade whose `Origin` is missing or another site's is `403`.
3. Any other request without `X-CSRF: 1`, with another site's `Origin` or with `Sec-Fetch-Site: cross-site` is `400`, whatever the method. GET is checked too, because some GETs have effects.

The client sends `X-CSRF: 1` on every request. The web host registers no CORS policy, so a page on another site can neither send the header nor read an answer. The own origin is the scheme from `X-Forwarded-Proto` and the `Host` header.

**Session checks.** An open page asks `/bff/session` every 30 seconds, and at once when a forwarded call answers `401`. When the session is gone (expired, logged out in another tab, or a new key), the page goes to the login page.

## Endpoint protection by host

Every host's endpoints are pinned in a test: a new endpoint, hub method or listener fails CI until it is classified. The snapshot files are the source of truth.

| Host | What it serves, and what each part needs | Anonymous | Source of truth |
|---|---|---|---|
| Web host | Pages (a signed-in user, plus the page's role policy), `/bff/session` (a signed-in user), `/api/ui/**` and `/hubs/ui` forwarded to the API (a signed-in user with a role), the static client files | `GET /`, `/login` (GET, POST, HEAD), `POST /auth/login`, `GET /auth/oidc`, `POST /auth/logout`, `/healthz`, `/readyz`, the static client files | `tests/CodingAgent.Web.IntegrationTests/Security/web-endpoint-authorization.snapshot.txt` |
| Pipeline API | Service surface: API keys. UI surface: the user token. Agent hub: an agent key, and the agent must own the job a method addresses | `/healthz`, `/readyz` | `tests/CodingAgent.Api.IntegrationTests/Security/api-endpoint-authorization.snapshot.txt`, `tests/CodingAgent.Web.UnitTests/Hubs/agent-hub-methods.snapshot.txt` |
| Scheduler | Health probes only | `/health`, `/healthz`, `/readyz` | `SchedulerHealthSurfaceTests`, `LayerBoundaryTests.HostSurface` |
| Job Controller | Health probes only | `/healthz`, `/readyz` | `JobControllerHealthSurfaceTests`, `LayerBoundaryTests.HostSurface` |
| Agent pod | Health probes only | `/healthz`, `/readyz`, `/startupz` | `HealthSurfaceTests` (agent), `LayerBoundaryTests.HostSurface` |

Probe responses carry only status fields. Every host listens on port 8080 only. No host serves OpenAPI, Swagger UI, a metrics scrape endpoint, directory browsing or the developer exception page, and the chart never runs a host in the `Development` environment. Each UI endpoint that takes a project, run, work-item, template or chat ID has a test that another project's user gets 404 (`IdorCoverageTests`), except the admin-only Settings endpoints, which have no project boundary.

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
        projectId: 6f1c2a9e-0000-0000-0000-000000000000   # payments-api
      - user: alice@example.com
        role: readonly
```

- A `group` binding matches one value of the groups claim exactly (case-sensitive). A `user` binding matches the username claim (case-insensitive).
- A user's role is the highest of `defaultRole` and every matching binding; on a project it is the highest of the global role and the project's bindings.
- A project binding names the project by its ID, shown on the project's Overview tab (Settings → Projects → project → Overview). Renaming a project does not change who can access it. Bindings are not checked against the project list: a binding to an ID that no project has opens nothing, and the profile page flags it.
- Binding changes take effect when the web pods restart. The chart annotates the pods with a checksum of the auth configuration, so `helm upgrade` restarts them.
- Invalid configurations stop `helm template` and the web host at startup: no login method, OIDC without issuer, client ID or client secret, `admin` bound to a project, or `project` used instead of `projectId`. The web host also stops on an unknown role or a `projectId` that is not a valid GUID.
- **Upgrade note:** Bindings that still use `project:` stop `helm template` and the web host at startup; replace them with `projectId: <id>` (the ID is shown on the project's Overview tab).

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

The web host reads the client's scheme from `X-Forwarded-Proto`, so TLS can end at the ingress. It reads `wss` and `ws`, which Traefik sends for the `/hubs/ui` WebSocket upgrade, as `https` and `http`. It does not trust `X-Forwarded-Host`; the ingress must pass the original `Host` header (the default for ingress-nginx and Traefik).

With a `tls` section on the Ingress, the web host also sends clients that reached the ingress over plain HTTP (`X-Forwarded-Proto: http`) to the same URL over HTTPS, so the login form never posts a password unencrypted. Requests without `X-Forwarded-Proto` (`kubectl port-forward`, probes) are served as they are. When TLS ends in front of the ingress and the ingress itself sees only HTTP, set `web.ingress.httpsRedirect: false`; otherwise every request is redirected. HTTPS responses carry HSTS (`Strict-Transport-Security`), so a browser that once opened the UI over HTTPS keeps using HTTPS.

Every response also carries `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, `Cross-Origin-Opener-Policy`, `Cross-Origin-Resource-Policy` and a Content Security Policy. The policy allows scripts only from the web host, the host page's inline scripts only with that response's nonce, WebAssembly compilation (`'wasm-unsafe-eval'`), and fonts, images and calls only from the web host. With Faro on (`Faro__CollectorUrl`), it also allows the two Faro bundles on `https://unpkg.com` (their exact URLs, not the whole CDN) and calls to the collector's origin. Styles allow `'unsafe-inline'`, because Blazor renders `style` attributes. The fonts are served from the web image, not from Google. `Cross-Origin-Resource-Policy` is `same-origin` everywhere except `/favicon.svg`, which is `cross-origin` so dashboards on other hosts can show it as the service icon.

Sessions are encrypted with one key from the `data-protection-key` of the `<release>-coding-agent-automation-web-user-token` Secret ([Deployment](deployment.md#user-token-secret)). Every web replica reads the same key, so a session works on all replicas and survives restarts; the web host needs no Redis.

Sticky sessions are optional. The UI runs in the browser, so any web replica serves its next request, and the UI hub reconnects through any replica. Existing stickiness annotations (Traefik's `traefik.ingress.kubernetes.io/service.sticky.cookie` on `web.service.annotations`, ingress-nginx's `nginx.ingress.kubernetes.io/affinity`) keep working.

With ingress-nginx, the UI's long calls pass through the ingress: chat launch (up to 2 minutes), triage, browse, dispatch and provider calls, and config import. Raise its defaults of 60 seconds and 1 MB:

```yaml
web:
  ingress:
    annotations:
      nginx.ingress.kubernetes.io/proxy-read-timeout: "300"
      nginx.ingress.kubernetes.io/proxy-body-size: "16m"
```

Traefik needs no change.

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

- A session lasts `auth.sessionDuration` from sign-in, without renewal. An open page checks its session every 30 seconds and goes to the login page once it has ended.
- Sessions survive web pod restarts: every replica reads the same key (see [Exposing the UI](#exposing-the-ui)).
- **Log out** ends the session in the web UI only. The identity provider's session stays, so the next sign-in may not ask for a password. Other open tabs of the same browser go to the login page at their next session check.

## Troubleshooting

| Symptom | Check |
|---|---|
| "You have no access yet" after sign-in | Open the profile page (`/user`). If no groups are listed, the provider does not put groups into the ID token (Keycloak mapper, Entra groups claim or overage). If groups are listed, add a binding with one of them. |
| The profile page says a binding "opens nothing: no project has this ID" | The `projectId` value must equal the ID shown on the project's Overview tab (Settings → Projects → project → Overview). |
| "Sign-in with … failed" | The web host logs the reason: wrong redirect URI, client ID, client secret or issuer, a provider it cannot reach, or a clock skew. |
| Redirect URI uses `http://` behind TLS | The ingress must send `X-Forwarded-Proto: https`. |
| Signed out on every request with several web replicas | All web pods must read the same `data-protection-key`; restart them after replacing the Secret. |
| Every UI call fails with `400`, or the live updates never connect (`403` on `/hubs/ui`) | A proxy in front of the web host drops the `X-CSRF` header or changes `Origin` or `Host`. The web host compares `Origin` with the `Host` header and the scheme from `X-Forwarded-Proto`. |
| Chat launch or config import fails with `504` or `413` behind ingress-nginx | Set `proxy-read-timeout` and `proxy-body-size` on the ingress (see [Exposing the UI](#exposing-the-ui)). |
