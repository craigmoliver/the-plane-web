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
az ad app federated-credential create --id $APP --parameters "{
  \"name\":\"main\",\"issuer\":\"https://token.actions.githubusercontent.com\",
  \"subject\":\"repo:$REPO:environment:production\",\"audiences\":[\"api://AzureADTokenExchange\"]}"
echo "AZURE_CLIENT_ID=$APP  AZURE_TENANT_ID=$(az account show --query tenantId -o tsv)"
```

## GitHub settings (repo → Settings → Secrets and variables → Actions)
Create an environment named **production** (optionally with required reviewers to approve each deploy).

| Variables | |
|---|---|
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP` | from above |
| `ADMIN_EMAIL` | bootstrap local admin (optional with Entra) |
| `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID` | work-account sign-in, see docs/entra.md |
| `LOCAL_LOGIN` | `false` for work accounts only (default `true`) |
| `ENTRA_ADMIN_OBJECT_IDS` | comma-separated Entra object IDs made admin (yours, at least, unless you use the Entra `Admin` app role or `ADMIN_EMAIL`) |

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
- **Database access:** PostgreSQL is reachable only inside the virtual network, so tools outside it (including the portal's query editor) can't connect. To inspect it, run a temporary container or VM in the VNet (e.g. `az container create` with `--vnet` and the `postgres` image, then `psql`), and delete it afterwards.
- **Admin access:** make sure at least one of `ADMIN_EMAIL`/`ADMIN_PASSWORD`, `ENTRA_ADMIN_OBJECT_IDS`, or the Entra `Admin` app role is set, or nobody can manage users.
