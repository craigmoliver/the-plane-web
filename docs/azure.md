# Deploying to Azure

Pushes to `main` run `.github/workflows/deploy.yml`: tests → image to GitHub Container Registry → deploy
a small VM (`infra/main.bicep`) → start it via Azure Run Command → smoke test.
The workflow is skipped until the repository variable `AZURE_CLIENT_ID` exists.

## What gets created (one resource group)
- **VM**: Standard_B1ms (1 vCPU, 2 GB RAM), Ubuntu 24.04, 30 GB SSD, running the same
  `docker-compose.yml` + Caddy (HTTPS) setup as the home server, with SQLite on its disk.
- **Public IP** with an Azure-assigned DNS name (`<label>.<region>.cloudapp.azure.com`); Caddy gets a
  real Let's Encrypt certificate for it automatically.
- **Network security group**: only ports 80 and 443 are reachable. **No SSH port is open.** Deploys and
  any later maintenance run through [Azure Run Command](https://learn.microsoft.com/azure/virtual-machines/run-command-overview),
  which executes over the control plane via the VM agent, not the network.
- **Recovery Services vault**: daily VM backup, kept 7 days.

Rough cost: **~$15 VM + ~$3 disk + ~$1–2 backup + a few cents IP ≈ $17–20/month.**

## One-time setup (Azure Cloud Shell or a machine with `az`)
```bash
SUB=<subscription id>; RG=planeweb-rg; LOC=eastus2; REPO=craigmoliver/the-plane-web
az account set -s $SUB
# Register resource providers once (the deploy identity only has resource-group rights and can't).
for p in Microsoft.Compute Microsoft.Network Microsoft.RecoveryServices; do
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

If app-registration creation is blocked in your directory (common on a company tenant), see
"Deploying with a company (CapTech) account" below for a managed-identity alternative that needs no
directory permissions beyond Contributor on the resource group.

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
| `ADMIN_PASSWORD` | bootstrap admin password |
| `ENTRA_CLIENT_SECRET` | from the Entra app registration |
| `GHCR_USERNAME`, `GHCR_TOKEN` | GitHub user + PAT with `read:packages` (the VM pulls the private image). Or make the package public and leave these empty. |

Then run **Actions → Deploy to Azure → Run workflow**. The run summary prints the URL and the Entra redirect URI;
add that redirect URI to the Entra app registration (docs/entra.md).

## Deploying with a company (CapTech) account
Hosting (this subscription) and sign-in (which directory's accounts are accepted) are independent: you
can host here while letting a company's employees sign in, by registering the Entra app in *their*
directory. See `scripts/setup-entra.sh` (macOS/Linux) or `scripts/setup-entra.ps1` (Windows) — each signs
in to the company tenant, creates or reuses the app registration with the right redirect URL, creates a
client secret, and saves everything to this repo's GitHub settings. Run after the first deploy, once you
have the VM's URL:
```powershell
.\scripts\setup-entra.ps1 -AppUrl https://<app url> -Tenant captech.com
```
If the company directory also blocks creating app registrations for your account, ask their IT to run it,
or to grant you the *Application Developer* directory role first.

## Notes
- **adsb.lol from Azure:** free feeds may throttle cloud IPs. The app falls back to adsb.fi automatically;
  check with Run Command: `az vm run-command invoke -g $RG -n planeweb-vm --command-id RunShellScript --scripts "docker logs planeweb --tail 100"`.
- **Custom domain:** point a CNAME at the Azure DNS name, set `PLANEWEB_HOST` accordingly on the VM
  (`/opt/planeweb/.env`) and redeploy, or add it as a second `site` block in `/opt/planeweb/Caddyfile`.
- **Admin access:** make sure at least one admin path is set: **both** `ADMIN_EMAIL` and `ADMIN_PASSWORD`
  (one alone does nothing), `ENTRA_ADMIN_OBJECT_IDS`, or the Entra `Admin` app role, or nobody can manage users.
- **No SSH key to manage:** each deploy generates a throwaway SSH key only to satisfy the Linux VM
  creation API; it's discarded immediately and the network security group has no rule allowing port 22
  anyway, so it's never actually usable.
- **Inspecting the VM** without SSH: `az vm run-command invoke -g $RG -n planeweb-vm --command-id RunShellScript --scripts "docker ps; docker compose -f /opt/planeweb/docker-compose.yml logs --tail 50"`.
- **Restoring from backup:** Azure portal → Recovery Services vault → Backup items → the VM → Restore VM.
