#!/usr/bin/env bash
#
# Provisions and deploys the TTB Label Verifier on Azure, end to end:
#   resource group → Azure OpenAI (gpt-4o vision) → Container Registry →
#   build & push image → Container Apps environment → Container App (public HTTPS).
#
# Requires: az CLI (logged in: `az login`), Docker.
# Nothing secret is hard-coded; the Azure OpenAI key is read at runtime and
# stored as a Container App secret.
#
# Usage:  ./deploy/deploy.sh
set -euo pipefail

# ---- Configuration (override via environment if you like) ----
SUFFIX="${SUFFIX:-$(openssl rand -hex 3)}"        # keeps globally-unique names unique
LOCATION="${LOCATION:-eastus2}"
RG="${RG:-rg-ttb-labelverifier}"
AOAI="${AOAI:-ttb-openai-$SUFFIX}"
MODEL="${MODEL:-gpt-4o}"
MODEL_VERSION="${MODEL_VERSION:-2024-11-20}"
MODEL_SKU="${MODEL_SKU:-Standard}"                # GlobalStandard if your quota allows it
ACR="${ACR:-ttbacr$SUFFIX}"
ACA_ENV="${ACA_ENV:-ttb-aca-env}"
APP="${APP:-ttb-label-verifier}"
IMAGE_TAG="${IMAGE_TAG:-v1}"

echo "Using suffix '$SUFFIX' in '$LOCATION' (resource group '$RG')"

# ---- Resource providers ----
for ns in Microsoft.CognitiveServices Microsoft.ContainerRegistry Microsoft.App Microsoft.OperationalInsights; do
  az provider register --namespace "$ns" --wait --only-show-errors
done

# ---- Resource group ----
az group create -n "$RG" -l "$LOCATION" -o none

# ---- Azure OpenAI + vision model deployment ----
az cognitiveservices account create -n "$AOAI" -g "$RG" -l "$LOCATION" \
  --kind OpenAI --sku S0 --custom-domain "$AOAI" --yes -o none
az cognitiveservices account deployment create -n "$AOAI" -g "$RG" \
  --deployment-name "$MODEL" \
  --model-name "$MODEL" --model-version "$MODEL_VERSION" --model-format OpenAI \
  --sku-name "$MODEL_SKU" --sku-capacity 30 -o none

AOAI_ENDPOINT=$(az cognitiveservices account show -n "$AOAI" -g "$RG" --query "properties.endpoint" -o tsv)
AOAI_KEY=$(az cognitiveservices account keys list -n "$AOAI" -g "$RG" --query "key1" -o tsv)

# ---- Container Registry + image ----
az acr create -n "$ACR" -g "$RG" --sku Basic --admin-enabled true -o none
LOGINSERVER=$(az acr show -n "$ACR" -g "$RG" --query loginServer -o tsv)
ACR_USER=$(az acr credential show -n "$ACR" -g "$RG" --query username -o tsv)
ACR_PASS=$(az acr credential show -n "$ACR" -g "$RG" --query "passwords[0].value" -o tsv)

# Built locally (ACR Tasks is disabled on some subscriptions).
docker build -t "$LOGINSERVER/labelverifier:$IMAGE_TAG" ./src/LabelVerifier
echo "$ACR_PASS" | docker login "$LOGINSERVER" -u "$ACR_USER" --password-stdin
docker push "$LOGINSERVER/labelverifier:$IMAGE_TAG"

# ---- Container Apps environment + app ----
az containerapp env create -n "$ACA_ENV" -g "$RG" -l "$LOCATION" -o none

az containerapp create -n "$APP" -g "$RG" --environment "$ACA_ENV" \
  --image "$LOGINSERVER/labelverifier:$IMAGE_TAG" \
  --registry-server "$LOGINSERVER" --registry-username "$ACR_USER" --registry-password "$ACR_PASS" \
  --target-port 8080 --ingress external \
  --cpu 1.0 --memory 2.0Gi --min-replicas 1 --max-replicas 3 \
  --secrets "aoai-key=$AOAI_KEY" \
  --env-vars "AzureOpenAI__Endpoint=$AOAI_ENDPOINT" \
             "AzureOpenAI__Deployment=$MODEL" \
             "AzureOpenAI__ApiKey=secretref:aoai-key" \
  -o none

FQDN=$(az containerapp show -n "$APP" -g "$RG" --query "properties.configuration.ingress.fqdn" -o tsv)
echo
echo "Deployed:  https://$FQDN"
