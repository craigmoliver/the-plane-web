# Deploying to Azure

Pushes to `main` run `.github/workflows/deploy.yml`: tests → image to GitHub Container Registry → `infra/main.bicep` → smoke test.
The workflow is skipped until the repository variable `AZURE_CLIENT_ID` exists.

## What gets created (one resource group)
- **Virtual network**: the app and database talk privately; PostgreSQL has **no public endpoint**.
- **Container App**: 1 always-on replica (0.5 vCPU / 1 GiB), HTTPS on `*.azurecontainerapps.io`.
- **PostgreSQL Flexible Server**: Burstable B1ms, 32 GB, 7-day backups.
- **Storage account / Azure Files share**: mounted at `/data` for trails, logo cache and keys (not the database).
- **Log Analytics** workspace (30 days).

Rough cost: ~$15–20 Container App + ~$13–15 Postgres + a few $ storage/logs ≈ **$30–40/month**.

## One-time setup (Azure Cloud Shell or a machine with `az`)
```bash
SUB=<subscription id>; RG=planeweb-rg; LOC=eastus; REPO=craigmoliver/the-plane-web
az account set -s $SUB
# Register resource providers once (the deploy identity only has resource-group rights and can't).
for p in Microsoft.App Microsoft.DBforPostgreSQL Microsoft.Network Microsoft.Storage Microsoft.OperationalInsights; do
  az provider register -n $p --wait
done
az group create -n $RG -l $LOC

# Identity GitHub Actions signs in as (OIDC; no secret stored in GitHub)
APP=$(az ad app create --display-name planeweb-deploy --query appId -o tsv)
az ad sp create --id $APP
az role assignment create --assignee $APP --role Contributor --scope /subscriptions/$SUB/resourceGroups/$RG
# The token subject must match exactly. Newer repos use immutable IDs (repo:owner@id/name@id), so ask GitHub:
PREFIX=$(gh api repos/$REPO/actions/oidc/customization/sub --jq .sub_claim_prefix)   # e.g. repo:craigmoliver@183209/the-plane-web@1403481538
az ad app federated-credential create --id $APP --parameters "{
  \"name\":\"production\",\"issuer\":\"https://token.actions.githubusercontent.com\",
  \"subject\":\"$PREFIX:environment:production\",\"audiences\":[\"api://AzureADTokenExchange\"]}"
echo "AZURE_CLIENT_ID=$APP  AZURE_TENANT_ID=$(az account show --query tenantId -o tsv)"
```

## GitHub settings (repo → Settings → Secrets and variables → Actions)
Create an environment named **production**. Under **Deployment branches and tags** choose *Selected branches* → `main`
(the workflow also refuses other refs), and optionally add required reviewers to approve each deploy.

| Variables | |
|---|---|
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP` | from above |
| `ADMIN_EMAIL` | bootstrap local admin; needs `ADMIN_PASSWORD` too (optional with Entra) |
| `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID` | work-account sign-in, see docs/entra.md |
| `LOCAL_LOGIN` | `false` for work accounts only (default `true`) |
| `ENTRA_ADMIN_OBJECT_IDS` | comma-separated Entra object IDs made admin (yours, at least, unless you use the Entra `Admin` app role or set both `ADMIN_EMAIL` and `ADMIN_PASSWORD`) |

| Secrets | |
|---|---|
| `POSTGRES_PASSWORD` | strong password; **keep it the same** on later deploys |
| `ADMIN_PASSWORD` | bootstrap admin password |
| `ENTRA_CLIENT_SECRET` | from the Entra app registration |
| `GHCR_USERNAME`, `GHCR_TOKEN` | GitHub user + PAT with `read:packages` (Azure pulls the private image). Or make the package public and leave these empty. |

Then run **Actions → Deploy to Azure → Run workflow**. The run summary prints the URL and the Entra redirect URI;
add that redirect URI to the Entra app registration (docs/entra.md).

## Notes
- **adsb.lol from Azure:** free feeds may throttle cloud IPs. The app falls back to adsb.fi automatically; check `az containerapp logs show -n planeweb -g $RG` for 429s after the first deploy.
- **Custom domain:** later, via `az containerapp hostname add` + managed certificate.
- **Database access:** PostgreSQL is reachable only inside the virtual network, so tools outside it (including the portal's query editor) can't connect. To inspect it, use a temporary container in its own subnet (the template's two subnets are delegated to Container Apps and PostgreSQL):
  ```bash
  az network vnet subnet create -g $RG --vnet-name planeweb-vnet -n debug --address-prefixes 10.40.3.0/28 \
    --delegations Microsoft.ContainerInstance/containerGroups
  az container create -g $RG -n pgdebug --image postgres:16-alpine --vnet planeweb-vnet --subnet debug \
    --command-line "sleep 3600" --os-type Linux --cpu 1 --memory 1
  az container exec -g $RG -n pgdebug --exec-command "psql -h <postgresHost output> -U planeweb -d planeweb"
  az container delete -g $RG -n pgdebug -y && az network vnet subnet delete -g $RG --vnet-name planeweb-vnet -n debug
  ```
- **Admin access:** make sure at least one admin path is set: **both** `ADMIN_EMAIL` and `ADMIN_PASSWORD` (one alone does nothing), `ENTRA_ADMIN_OBJECT_IDS`, or the Entra `Admin` app role, or nobody can manage users.
