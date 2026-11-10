using './main.bicep'

// Replace the placeholders before deploying. Nothing in this file is secret.
param environmentName = 'dev'
param containerImage = 'ghcr.io/OWNER/energy-monitor:latest'
// Google: 'https://accounts.google.com'. Entra ID: 'https://login.microsoftonline.com/TENANT_ID/v2.0'.
param authAuthority = 'https://accounts.google.com'
param authClientId = 'CLIENT_ID.apps.googleusercontent.com'
param users = [
  { subject: 'owner@example.com', role: 'admin' }
]
param telegramChatIds = []
