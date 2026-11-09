using './main.bicep'

// Replace the placeholders before deploying. Nothing in this file is secret.
param environmentName = 'prod'
param containerImage = 'ghcr.io/OWNER/energy-monitor:latest'
param authAuthority = 'https://login.microsoftonline.com/TENANT_ID/v2.0'
param authClientId = '00000000-0000-0000-0000-000000000000'
param users = [
  { subject: '00000000-0000-0000-0000-000000000000', role: 'admin' }
  { subject: '00000000-0000-0000-0000-000000000000', role: 'viewer' }
]
param telegramChatIds = []
