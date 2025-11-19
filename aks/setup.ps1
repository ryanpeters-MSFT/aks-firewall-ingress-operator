# variables
$group = "rg-aks-firewall-dnat"
$location = "eastus2"
$vnetName = "vnet"
$aksSubnet = "aks"
$fwSubnet = "AzureFirewallSubnet"
$clusterName = "firewallcluster"
$fwName = "firewall"
$fwPublicIp = "fwpip"
$fwIpConfig = "fwconfig"

# get subscription id
$subscription = az account show --query id -o tsv

# create resource group
az group create -n $group -l $location

# create vnet with aks subnet
az network vnet create -g $group -n $vnetName -l $location --address-prefixes 10.42.0.0/16 --subnet-name $aksSubnet --subnet-prefix 10.42.0.0/24

# create firewall subnet
az network vnet subnet create -g $group --vnet-name $vnetName -n $fwSubnet --address-prefix 10.42.1.0/24

# get aks subnet id
$subnetId = az network vnet subnet show -g $group --vnet-name $vnetName -n $aksSubnet --query id -o tsv

# create aks cluster
az aks create -g $group -n $clusterName -l $location -c 1 --vnet-subnet-id $subnetId --network-plugin azure

# create public ip for firewall
az network public-ip create -g $group -n $fwPublicIp -l $location --sku Standard

# create azure firewall
az network firewall create -g $group -n $fwName -l $location

# configure firewall ip
az network firewall ip-config create -g $group -f $fwName -n $fwIpConfig --public-ip-address $fwPublicIp --vnet-name $vnetName

# update firewall
az network firewall update -g $group -n $fwName

# get credentials for aks cluster
az aks get-credentials -g $group -n $clusterName --overwrite-existing

"AZURE_SUBSCRIPTION_ID = $subscription"
"AZURE_RESOURCE_GROUP = $group"
"AZURE_FIREWALL_NAME = $fwName"

$Env:AZURE_SUBSCRIPTION_ID = $subscription
$Env:AZURE_RESOURCE_GROUP = $group
$Env:AZURE_FIREWALL_NAME = $fwName