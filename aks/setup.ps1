# variables
$group = "rg-aks-firewall-dnat"
$location = "eastus2"
$vnetName = "vnet"
$aksSubnet = "aks"
$fwSubnet = "AzureFirewallSubnet"
$registry = "binarydad" # note this script does not create an ACR, do that separately
$clusterName = "firewallcluster"
$fwName = "firewall"
$fwPublicIp = "fwpip"
$fwIpConfig = "fwconfig"
$fwIdentity = "fwoperator"

# get subscription id
$subscription = az account show --query id -o tsv

# create resource group
$groupId = az group create -n $group -l $location -o tsv --query id

# create vnet with aks subnet
az network vnet create `
    -g $group `
    -n $vnetName `
    --address-prefixes 10.42.0.0/16 `
    --subnet-name $aksSubnet `
    --subnet-prefix 10.42.0.0/24

# create firewall subnet
az network vnet subnet create `
    -g $group `
    --vnet-name $vnetName `
    -n $fwSubnet `
    --address-prefix 10.42.1.0/24

# get aks subnet id
$subnetId = az network vnet subnet show -g $group --vnet-name $vnetName -n $aksSubnet --query id -o tsv

# create a managed identity for fedarated credential
$principalId = az identity create -g $group -n $fwIdentity -o tsv --query principalId

az role assignment create `
    --assignee-object-id $principalId `
    --assignee-principal-type ServicePrincipal `
    --scope $groupId `
    --role "Contributor"

# create aks cluster
az aks create `
    -g $group `
    -n $clusterName `
    -c 1 `
    --node-vm-size Standard_D8s_v6 `
    --enable-oidc-issuer `
    --enable-workload-identity `
    --vnet-subnet-id $subnetId `
    --attach-acr $registry `
    --network-plugin azure

# get the OIDC issuer URL
$oidcIssuerUrl = az aks show -n $clusterName -g $group --query "oidcIssuerProfile.issuerUrl" -o tsv

# create user assigned managed identity
az identity federated-credential create `
    --name "azure-alb-identity" `
    --identity-name $fwIdentity `
    -g $group `
    --issuer $oidcIssuerUrl `
    --subject "system:serviceaccount:firewall-sync:firewall-operator"

# create public ip for firewall
az network public-ip create -g $group -n $fwPublicIp --sku Standard

# create azure firewall
az network firewall create -g $group -n $fwName

# configure firewall ip
az network firewall ip-config create -g $group -f $fwName -n $fwIpConfig --public-ip-address $fwPublicIp --vnet-name $vnetName

# update firewall
az network firewall update -g $group -n $fwName

# get credentials for aks cluster
az aks get-credentials -g $group -n $clusterName --overwrite-existing

# get the client id of the user assigned managed identity
$clientId = az identity show -n $fwIdentity -g $group --query clientId -o tsv

# get the public ip address of the firewall
$firewallPublicIp = az network public-ip show -g $group -n $fwPublicIp --query ipAddress -o tsv

"AZURE_SUBSCRIPTION_ID = $subscription"
"AZURE_RESOURCE_GROUP = $group"
"AZURE_FIREWALL_NAME = $fwName"
"Operator Client ID = $clientId"
"Firewall Public IP = $firewallPublicIp"

$Env:AZURE_SUBSCRIPTION_ID = $subscription
$Env:AZURE_RESOURCE_GROUP = $group
$Env:AZURE_FIREWALL_NAME = $fwName