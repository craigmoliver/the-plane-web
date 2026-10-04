# Microsoft work-account sign-in (Entra ID)

Anyone in your organization's Entra tenant can sign in; their account is created on first sign-in.
Accounts from other tenants and personal Microsoft accounts are refused.

## 1. Register the app (one time)
1. Azure portal → **Microsoft Entra ID** → **App registrations** → **New registration**.
2. Name: `The Plane Web`. Supported account types: **Accounts in this organizational directory only (single tenant)**.
3. Redirect URI: platform **Web**, URL `https://<your host>/signin-oidc`.
   Add one per place you run it (home server and Azure). Microsoft requires HTTPS except for `http://localhost`.
4. After creating it, note the **Application (client) ID** and **Directory (tenant) ID**.
5. **Certificates & secrets** → **New client secret**. Copy the value now; it is shown once. Note its expiry date.

## 2. (Optional) Admin role
1. App registration → **App roles** → **Create app role**: display name `Admin`, value `Admin`, allowed member types **Users/Groups**.
2. **Enterprise applications** → The Plane Web → **Users and groups** → assign the people (or a group) to `Admin`.

Alternatively list admin emails in `PlaneWeb__Auth__Entra__AdminEmails__0`, `__1`, … or use **Make admin** on `/admin/users`.
Admin granted by role or list is added at sign-in; removing it is done on `/admin/users`.

## 3. Configure the app
In `.env` (home server) or the Azure app settings:

```
PlaneWeb__Auth__Entra__TenantId=<directory (tenant) id>
PlaneWeb__Auth__Entra__ClientId=<application (client) id>
PlaneWeb__Auth__Entra__ClientSecret=<secret value>
```

Behind a reverse proxy (Caddy, Azure Container Apps) also set `PlaneWeb__TrustForwardedHeaders=true`
so the redirect URI is built with `https`.

To use only work accounts, set `PlaneWeb__Auth__LocalLogin=false` after confirming an Entra admin can sign in.

## Removing access
- Someone leaving the company: disabling them in Entra stops new sign-ins. Also use **Disable** or
  **Sign out everywhere** on `/admin/users` to end existing sessions (within a minute).
