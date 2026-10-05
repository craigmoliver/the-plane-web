#!/usr/bin/env bash
# Registers The Plane Web in a company Entra ID directory (e.g. CapTech) so its employees can sign in,
# then stores the values in GitHub so the next deploy turns on Microsoft sign-in.
#
# Needs: Azure CLI (az), and GitHub CLI (gh) signed in to an account with admin on the repo.
#        Permission to create app registrations in the directory (or have IT run this).
#
# Usage: ./scripts/setup-entra.sh https://<app url>  [tenant domain or id, e.g. captech.com]
set -euo pipefail

APP_URL="${1:?Usage: $0 https://<app url> [tenant]}"
TENANT="${2:-}"
REPO="${REPO:-craigmoliver/the-plane-web}"
NAME="${APP_NAME:-The Plane Web}"
APP_URL="${APP_URL%/}"
[[ "$APP_URL" == https://* ]] || { echo "The app URL must start with https://"; exit 1; }

for c in az gh; do command -v $c >/dev/null || { echo "Missing '$c'. Install it first."; exit 1; }; done
gh auth status >/dev/null 2>&1 || gh auth login

echo "== Signing in to Azure =="
# --allow-no-subscriptions: a person can be allowed to register apps in a directory without having
# access to any Azure subscription there (a common split at a company). account show still works
# (it uses a placeholder context) and still reports the real tenant id.
if [[ -n "$TENANT" ]]; then az login --tenant "$TENANT" --allow-no-subscriptions -o none
else az login --allow-no-subscriptions -o none; fi
TENANT_ID=$(az account show --query tenantId -o tsv)
ME=$(az ad signed-in-user show --query id -o tsv)
echo "Directory: $TENANT_ID   You: $(az ad signed-in-user show --query userPrincipalName -o tsv)"

echo "== App registration =="
NEW_URI="$APP_URL/signin-oidc"
APP_ID=$(az ad app list --display-name "$NAME" --query "[0].appId" -o tsv)
if [[ -z "$APP_ID" ]]; then
  APP_ID=$(az ad app create --display-name "$NAME" --sign-in-audience AzureADMyOrg \
    --web-redirect-uris "$NEW_URI" --query appId -o tsv)
  echo "Created $APP_ID"
else
  # --web-redirect-uris replaces the whole list; merge in the new one instead of dropping any
  # existing callback (e.g. a home-server deployment using the same app registration).
  # Avoids mapfile (not in macOS's bundled Bash 3.2) and checks the lookup explicitly so a failure
  # can't silently fall through to an update that replaces the existing callbacks.
  existing=$(az ad app show --id "$APP_ID" --query "web.redirectUris[]" -o tsv)
  URIS=()
  while IFS= read -r line; do [[ -n "$line" ]] && URIS+=("$line"); done <<< "$existing"
  found=0
  for u in ${URIS[@]+"${URIS[@]}"}; do [[ "$u" == "$NEW_URI" ]] && found=1; done
  [[ $found -eq 0 ]] && URIS+=("$NEW_URI")
  az ad app update --id "$APP_ID" --web-redirect-uris "${URIS[@]}"
  echo "Reusing $APP_ID (redirect URIs: ${URIS[*]})"
fi
# Enterprise application (lets IT require assignment / restrict to a group later)
az ad sp show --id "$APP_ID" -o none 2>/dev/null || az ad sp create --id "$APP_ID" -o none

echo "== Client secret (valid 1 year) =="
SECRET=$(az ad app credential reset --id "$APP_ID" --display-name planeweb --years 1 --append --query password -o tsv)
EXPIRES=$(date -d '+1 year' +%F 2>/dev/null || date -v+1y +%F)

echo "== Saving to GitHub ($REPO) =="
gh variable set ENTRA_TENANT_ID -R "$REPO" -b "$TENANT_ID"
gh variable set ENTRA_CLIENT_ID -R "$REPO" -b "$APP_ID"
gh variable set ENTRA_ADMIN_OBJECT_IDS -R "$REPO" -b "$ME"
printf '%s' "$SECRET" | gh secret set ENTRA_CLIENT_SECRET -R "$REPO" --env production
unset SECRET

echo "== Triggering a deploy =="
gh workflow run deploy.yml -R "$REPO" --ref main

cat <<EOF

Done.
  Tenant:   $TENANT_ID
  Client:   $APP_ID
  Admin:    you ($ME)
  Redirect: $APP_URL/signin-oidc
  Secret expires about $EXPIRES; rerun this script before then.

The deploy takes a few minutes: gh run watch -R $REPO
Then open $APP_URL and choose "Sign in with your work account".
To limit access to a group: Entra ID > Enterprise applications > $NAME >
  Properties > Assignment required = Yes, then Users and groups > add the group.
EOF
