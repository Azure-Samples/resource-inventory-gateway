import { roleAssignmentInfo } from './managed-identity.bicep'

targetScope = 'subscription'

@description('Role assignments to create for the Subscription.')
param roleAssignments roleAssignmentInfo[] = []

resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleAssignment in roleAssignments: {
    name: guid(subscription().id, roleAssignment.principalId, roleAssignment.roleDefinitionId)
    scope: subscription()
    properties: {
      principalId: roleAssignment.principalId
      roleDefinitionId: roleAssignment.roleDefinitionId
      principalType: roleAssignment.principalType
    }
  }
]
