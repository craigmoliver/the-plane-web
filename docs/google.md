# Google sign-in

Lets allowed people sign in with their Google account. Who's allowed is managed on `/admin/google-allowlist`,
not in config — so access can be changed without a redeploy.

## 1. Create a Google OAuth client (one time)
1. [Google Cloud Console](https://console.cloud.google.com/) → create or pick a project → **APIs & Services** → **OAuth consent screen**.
   - User type: **External** (anyone with a Google account can attempt sign-in; the allowlist below decides who actually gets in).
   - Scopes: the defaults (`openid`, `email`, `profile`) are enough.
2. **APIs & Services** → **Credentials** → **Create credentials** → **OAuth client ID**.
   - Application type: **Web application**.
   - Authorized redirect URI: `https://<your host>/signin-google`. Add one per place you run it (home server and Azure).
     Google requires HTTPS except for `http://localhost`.
3. Note the **Client ID** and **Client secret**.

## 2. Configure the app
In `.env` (home server) or the Azure app settings:

```
PlaneWeb__Auth__Google__ClientId=<client id>
PlaneWeb__Auth__Google__ClientSecret=<client secret>
```

Behind a reverse proxy (Caddy, Azure Container Apps) also set `PlaneWeb__TrustForwardedHeaders=true`
so the redirect URI is built with `https`.

## 3. Who can sign in
The allowlist lives in the database, managed on `/admin/google-allowlist` (admin only). It's seeded once, the
first time the app starts with Google enabled (a separate marker tracks this, so deliberately emptying the
list later — see "Removing access" below — is never undone by a later restart), from:

```
PlaneWeb__Auth__Google__AdminEmails__0=...   # made Admin
PlaneWeb__Auth__Google__AdminEmails__1=...
PlaneWeb__Auth__Google__AllowedEmails__0=... # regular User
```

After that first run, these config values are no longer read — add, remove, or promote people on the
allowlist page instead.

Only verified Google emails are accepted. If the email matches exactly one existing local account, Google
sign-in is linked to it automatically (the account keeps its password too). If it matches more than one local
account (emails aren't required to be unique in this app), sign-in is refused until an admin resolves the
ambiguity.

**Granting admin:** checking "Admin" (when adding) or **Make admin** (later) applies immediately to anyone
already signed in with that email via Google, instead of waiting for their next sign-in.

**Demoting:** there's no **Remove admin** action here on purpose. Role membership alone can't tell a
Google-sourced Admin grant apart from one made on `/admin/users` (or granted by Entra), so automatically
revoking it here could silently strip an unrelated grant. Removing someone from the allowlist only stops
*future* Google sign-ins from (re-)granting admin; if they already have the Admin role and you want to take
it away, do that on `/admin/users`.

## Removing access
Remove the email from `/admin/google-allowlist` to stop new sign-ins. Use **Disable** or **Sign out everywhere**
on `/admin/users` to end an existing session (within a minute) and, if they're an Admin, **Remove admin** there too.
