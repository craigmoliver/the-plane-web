// The Plane Web on Azure: a single small VM running the existing Docker Compose setup
// (SQLite + Caddy for HTTPS), deployed into an existing resource group.
// No SSH is reachable (NSG allows only 80/443); deploys and maintenance use Azure Run Command,
// which works over the control plane via the VM agent and needs no open port.
targetScope = 'resourceGroup'

@description('Short name used to derive resource names (lowercase letters/digits).')
@minLength(3)
@maxLength(12)
param name string = 'planeweb'

param location string = resourceGroup().location

@allowed(['Standard_B1s', 'Standard_B1ms', 'Standard_B2s'])
param vmSize string = 'Standard_B1ms'

param osDiskGb int = 30

@description('Admin username for the VM. No password/SSH access is exposed (NSG blocks port 22); a one-time ephemeral key is supplied only to satisfy the Linux VM creation API.')
param adminUsername string = 'adminuser'

@secure()
@description('Ephemeral SSH public key, generated fresh per deploy and never saved (see deploy.yml). Unusable in practice: port 22 is not open.')
param adminSshPublicKey string

@description('Globally unique DNS label for the public IP, e.g. planeweb-ab12cd. Produces <label>.<region>.cloudapp.azure.com.')
param dnsLabel string = toLower('${name}-${uniqueString(resourceGroup().id)}')

param backupRetentionDays int = 7

resource vnet 'Microsoft.Network/virtualNetworks@2024-01-01' = {
  name: '${name}-vnet'
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.50.0.0/24'] }
    subnets: [
      {
        name: 'vm'
        properties: { addressPrefix: '10.50.0.0/27' }
      }
    ]
  }
}

// Only 80/443 inbound. Azure's default rules (priority 65000+) include AllowVnetInBound, which would
// otherwise let anything else in this VNet reach port 22 and others; the explicit deny below closes
// that gap so only 80/443 is reachable from any source, matching the stated policy exactly.
resource nsg 'Microsoft.Network/networkSecurityGroups@2024-01-01' = {
  name: '${name}-nsg'
  location: location
  properties: {
    securityRules: [
      {
        name: 'AllowHttpHttps'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: '*'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRanges: ['80', '443']
        }
      }
      {
        name: 'DenyAllOtherInbound'
        properties: {
          priority: 4096 // just below the max custom-rule priority; still well ahead of Azure's 65000+ defaults
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: '*'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ]
  }
}

resource pip 'Microsoft.Network/publicIPAddresses@2024-01-01' = {
  name: '${name}-ip'
  location: location
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
    dnsSettings: { domainNameLabel: dnsLabel }
  }
}

resource nic 'Microsoft.Network/networkInterfaces@2024-01-01' = {
  name: '${name}-nic'
  location: location
  properties: {
    ipConfigurations: [
      {
        name: 'ipconfig1'
        properties: {
          privateIPAllocationMethod: 'Dynamic'
          subnet: { id: '${vnet.id}/subnets/vm' }
          publicIPAddress: { id: pip.id }
        }
      }
    ]
    networkSecurityGroup: { id: nsg.id }
  }
}

resource vm 'Microsoft.Compute/virtualMachines@2024-07-01' = {
  name: '${name}-vm'
  location: location
  identity: { type: 'SystemAssigned' } // lets Run Command execute without any stored credential
  properties: {
    hardwareProfile: { vmSize: vmSize }
    osProfile: {
      computerName: '${name}-vm'
      adminUsername: adminUsername
      customData: base64(loadTextContent('cloud-init.yaml'))
      linuxConfiguration: {
        // Required by the VM creation API; irrelevant in practice because the NSG above has no rule for port 22.
        disablePasswordAuthentication: true
        ssh: {
          publicKeys: [
            { path: '/home/${adminUsername}/.ssh/authorized_keys', keyData: adminSshPublicKey }
          ]
        }
      }
    }
    storageProfile: {
      imageReference: {
        publisher: 'Canonical'
        offer: 'ubuntu-24_04-lts'
        sku: 'server'
        version: 'latest'
      }
      osDisk: {
        createOption: 'FromImage'
        diskSizeGB: osDiskGb
        managedDisk: { storageAccountType: 'StandardSSD_LRS' }
      }
    }
    networkProfile: {
      networkInterfaces: [{ id: nic.id }]
    }
  }
}

// ---- daily backup, kept 7 days by default ----
resource vault 'Microsoft.RecoveryServices/vaults@2024-04-01' = {
  name: '${name}-vault'
  location: location
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    publicNetworkAccess: 'Enabled'
  }
}

resource backupPolicy 'Microsoft.RecoveryServices/vaults/backupPolicies@2024-04-01' = {
  parent: vault
  name: 'daily-${backupRetentionDays}d'
  properties: {
    backupManagementType: 'AzureIaasVM'
    schedulePolicy: {
      schedulePolicyType: 'SimpleSchedulePolicy'
      scheduleRunFrequency: 'Daily'
      scheduleRunTimes: ['2026-01-01T06:00:00Z'] // time of day only; date is ignored
    }
    retentionPolicy: {
      retentionPolicyType: 'LongTermRetentionPolicy'
      dailySchedule: {
        retentionTimes: ['2026-01-01T06:00:00Z']
        retentionDuration: { count: backupRetentionDays, durationType: 'Days' }
      }
    }
    timeZone: 'UTC'
  }
}

resource protectedItem 'Microsoft.RecoveryServices/vaults/backupFabrics/protectionContainers/protectedItems@2024-04-01' = {
  name: '${vault.name}/Azure/iaasvmcontainer;iaasvmcontainerv2;${resourceGroup().name};${vm.name}/vm;iaasvmcontainerv2;${resourceGroup().name};${vm.name}'
  properties: {
    protectedItemType: 'Microsoft.Compute/virtualMachines'
    policyId: backupPolicy.id
    sourceResourceId: vm.id
  }
}

output url string = 'https://${pip.properties.dnsSettings.fqdn}'
output host string = pip.properties.dnsSettings.fqdn
output entraRedirectUri string = 'https://${pip.properties.dnsSettings.fqdn}/signin-oidc'
output vmName string = vm.name
