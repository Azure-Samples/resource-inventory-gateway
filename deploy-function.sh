#!/bin/bash

# Variables
RESOURCE_GROUP=$1
LOCATION=$2
LOCAL_BUILD=$3
ZIP_URL="https://github.com/yodobrin/resource-inventory/releases/latest/download/functionapp.zip"

# Check if resource group and location are provided
if [ -z "$RESOURCE_GROUP" ] || [ -z "$LOCATION" ]; then
    echo "Usage: $0 <RESOURCE_GROUP> <LOCATION> <LOCAL_BUILD>" >&2
    echo "Options:" >&2
    echo "  <RESOURCE_GROUP>  The name of the resource group to deploy the resources to." >&2
    echo "  <LOCATION>        The Azure region to deploy the resources to." >&2
    echo "  <LOCAL_BUILD>     (Optional) Set to 1 to build the Function App locally, 0 to download the package. Default is 0." >&2
    echo "Example: $0 my-resource-group eastus" >&2
    exit 1
fi

if [ -z "$LOCAL_BUILD" ]; then
    LOCAL_BUILD=0
fi

# Deploy the necessary infrastructure using Bicep
echo "Starting infrastructure deployment..." >&2

userPrincipalId=$(az rest --method GET --uri "https://graph.microsoft.com/v1.0/me" | jq -r '.id')

deploymentName="infra-$RESOURCE_GROUP-$(date +%s)"
deploymentOutputs=$(
    az deployment sub create \
        --name "$deploymentName" \
        --location "$LOCATION" \
        --template-file './infra/main.bicep' \
        --parameters './infra/main.parameters.json' \
        --parameters location="$LOCATION" \
        --parameters resourceGroupName="$RESOURCE_GROUP" \
        --parameters userPrincipalId="$userPrincipalId" \
        --query properties.outputs -o json
)

echo "$deploymentOutputs" >&2

functionAppName=$(echo "$deploymentOutputs" | jq -r '.functionAppName.value')
functionAppUrl=$(echo "$deploymentOutputs" | jq -r '.functionAppUrl.value')
storageAccountName=$(echo "$deploymentOutputs" | jq -r '.storageAccountName.value')
functionAppReleaseContainerName=$(echo "$deploymentOutputs" | jq -r '.functionAppReleaseContainerName.value')

# Download and deploy the Function App package
if [ "$LOCAL_BUILD" -eq 0 ]; then
    echo "Downloading the Function App package..." >&2
    curl -L "$ZIP_URL" -o ./functionapp.zip
else
    # Build the Function App locally, package it, and deploy it
    echo "Building the Function App locally..." >&2

    dotnet restore ./src/ResourceInventory
    dotnet build ./src/ResourceInventory --configuration Release --no-restore
    dotnet publish ./src/ResourceInventory --configuration Release --output ./artifacts --no-restore

    pushd ./artifacts || exit
    rm -f ../functionapp.zip
    zip -r ../functionapp.zip ./
    popd || exit
fi

echo "Uploading the Function App package to the storage account..." >&2
az storage blob upload \
    --account-name "$storageAccountName" \
    --container-name "$functionAppReleaseContainerName" \
    --name functionapp.zip \
    --file ./functionapp.zip \
    --overwrite true \
    --auth-mode login

echo "Configuring the Function App to run from the package..." >&2

blobUrl=$(az storage blob url \
    --account-name "$storageAccountName" \
    --container-name "$functionAppReleaseContainerName" \
    --name functionapp.zip \
    --output tsv \
    --auth-mode login)

az functionapp config appsettings set \
    --name "$functionAppName" \
    --resource-group "$RESOURCE_GROUP" \
    --settings WEBSITE_RUN_FROM_PACKAGE="$blobUrl"

echo "Deployment completed. Function App URL: https://$functionAppUrl"
