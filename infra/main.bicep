// The Plane Web on Azure: Container Apps + PostgreSQL Flexible Server + Azure Files for /data.
// Deployed by .github/workflows/deploy.yml into an existing resource group.
targetScope = 'resourceGroup'

@description('Short name used to derive resource names (lowercase letters/digits).')
@minLength(3)
@maxLength(12)
param name string = 'planeweb'

param location string = resourceGroup().location

@description('Container image, e.g. ghcr.io/owner/the-plane-web:<sha>.')
param image string

@description('Registry credentials; leave empty if the image is public.')
param registryServer string = 'ghcr.io'
param registryUsername string = ''
@secure()
param registryPassword string = ''

@description('PostgreSQL administrator password.')
@secure()
param postgresPassword string

@description('Bootstrap admin (local account). Optional when work-account admins are configured.')
param adminEmail string = ''
@secure()
param adminPassword string = ''

@description('Microsoft Entra sign-in. Leave tenant/client empty to disable.')
param entraTenantId string = ''
param entraClientId string = ''
@secure()
param entraClientSecret string = ''
@description('Entra object IDs made admin on sign-in.')
param entraAdminObjectIds array = []

@description('Allow local email/password accounts alongside work accounts.')
param localLogin bool = true

param timeZone string = 'America/New_York'

var suffix = uniqueString(resourceGroup().id)
var pgServerName = '${name}-pg-${suffix}'
var storageName = take('${name}st${suffix}', 24)
var hasRegistryAuth = !empty(registryUsername)
var hasEntra = !empty(entraTenantId) && !empty(entraClientId)

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ---- storage for /data (trails, logo cache, data-protection keys; NOT the database) ----
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: 'data'
  properties: { shareQuota: 5 }
}

// ---- PostgreSQL ----
resource pg 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: pgServerName
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    administratorLogin: 'planeweb'
    administratorLoginPassword: postgresPassword
    storage: { storageSizeGB: 32 }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' }
  }
}

resource pgDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: pg
  name: 'planeweb'
}

// 0.0.0.0 = "allow Azure services": Container Apps without a custom VNet has no fixed outbound IP.
// TLS is enforced by the server; the password is the access control.
resource pgAllowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: pg
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ---- Container Apps ----
resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${name}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource envStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: env
  name: 'data'
  properties: {
    azureFile: {
      accountName: storage.name
      accountKey: storage.listKeys().keys[0].value
      shareName: share.name
      accessMode: 'ReadWrite'
    }
  }
}

var baseSecrets = [
  { name: 'pg-password', value: postgresPassword }
]
var adminSecrets = empty(adminPassword) ? [] : [ { name: 'admin-password', value: adminPassword } ]
var entraSecrets = hasEntra && !empty(entraClientSecret) ? [ { name: 'entra-secret', value: entraClientSecret } ] : []
var registrySecrets = hasRegistryAuth ? [ { name: 'registry-password', value: registryPassword } ] : []

var baseEnv = [
  { name: 'TZ', value: timeZone }
  { name: 'PlaneWeb__Database', value: 'Postgres' }
  { name: 'PlaneWeb__Postgres__Host', value: pg.properties.fullyQualifiedDomainName }
  { name: 'PlaneWeb__Postgres__Database', value: pgDb.name }
  { name: 'PlaneWeb__Postgres__Username', value: 'planeweb' }
  { name: 'PlaneWeb__Postgres__Password', secretRef: 'pg-password' }
  { name: 'PlaneWeb__TrustForwardedHeaders', value: 'true' }
  { name: 'PlaneWeb__Auth__LocalLogin', value: string(localLogin) }
]
var adminEnv = empty(adminEmail) || empty(adminPassword) ? [] : [
  { name: 'PlaneWeb__Auth__AdminEmail', value: adminEmail }
  { name: 'PlaneWeb__Auth__AdminPassword', secretRef: 'admin-password' }
]
var entraAdminEnv = [for (id, i) in entraAdminObjectIds: { name: 'PlaneWeb__Auth__Entra__AdminObjectIds__${i}', value: id }]
var entraEnv = !hasEntra ? [] : concat([
  { name: 'PlaneWeb__Auth__Entra__TenantId', value: entraTenantId }
  { name: 'PlaneWeb__Auth__Entra__ClientId', value: entraClientId }
], empty(entraClientSecret) ? [] : [
  { name: 'PlaneWeb__Auth__Entra__ClientSecret', secretRef: 'entra-secret' }
], entraAdminEnv)

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  dependsOn: [ pgAllowAzure ]
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        // Blazor Server keeps a circuit per browser; one replica, so no affinity needed today.
      }
      secrets: concat(baseSecrets, adminSecrets, entraSecrets, registrySecrets)
      registries: hasRegistryAuth ? [
        {
          server: registryServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ] : []
    }
    template: {
      containers: [
        {
          name: 'planeweb'
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(baseEnv, adminEnv, entraEnv)
          volumeMounts: [
            { volumeName: 'data', mountPath: '/data' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/healthz', port: 8080 }
              initialDelaySeconds: 15
              periodSeconds: 30
            }
            {
              type: 'Startup'
              httpGet: { path: '/healthz', port: 8080 }
              periodSeconds: 5
              failureThreshold: 30 // up to 2.5 min for first-run migrations
            }
          ]
        }
      ]
      // Exactly one always-on replica: the app polls flight data in the background and
      // keeps in-memory state (pollers, trails), so it must neither scale to zero nor out.
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
      volumes: [
        {
          name: 'data'
          storageType: 'AzureFile'
          storageName: envStorage.name
          // The image runs as the non-root "app" user (uid 1654).
          mountOptions: 'uid=1654,gid=1654,dir_mode=0750,file_mode=0640,mfsymlinks,nobrl'
        }
      ]
    }
  }
}

output url string = 'https://${app.properties.configuration.ingress.fqdn}'
output entraRedirectUri string = 'https://${app.properties.configuration.ingress.fqdn}/signin-oidc'
output postgresHost string = pg.properties.fullyQualifiedDomainName
