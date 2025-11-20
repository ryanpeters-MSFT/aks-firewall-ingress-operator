# AKS Firewall Ingress Operator

A Kubernetes operator that automatically manages Azure Firewall DNAT rules for LoadBalancer services in Azure Kubernetes Service (AKS). This operator watches for Kubernetes LoadBalancer services with a special annotation and automatically creates corresponding DNAT rules on an Azure Firewall to expose those services to the internet.

## Overview

When running AKS with Azure Firewall as the egress solution, you may want to expose internal LoadBalancer services through the firewall's public IP address. This operator automates the creation and management of DNAT (Destination Network Address Translation) rules on Azure Firewall, eliminating the need for manual firewall configuration.

### How It Works

1. **Service Watching**: The operator continuously watches all Kubernetes LoadBalancer services across all namespaces
2. **Annotation Detection**: When a service has the `azure.firewall/public-ip` annotation, the operator processes it
3. **LoadBalancer IP Acquisition**: The operator waits for the service to receive an internal LoadBalancer IP from AKS
4. **DNAT Rule Creation**: Automatically creates DNAT rules on the Azure Firewall that map:
   - `<Firewall Public IP>:<Service Port>` → `<LoadBalancer Internal IP>:<Service Port>`
5. **Rule Management**: Updates rules when services are modified and removes rules when services are deleted
6. **Multi-Port Support**: Creates separate DNAT rules for each port defined in the service

### Architecture

```
Internet → Azure Firewall Public IP:80 → [DNAT Rule] → AKS LoadBalancer IP:80 → Service → Pods
```

The operator uses:
- **Kubernetes Client SDK** to watch service events
- **Azure SDK** to manage Azure Firewall configuration
- **Azure Workload Identity** for secure authentication to Azure

## Prerequisites

- Azure Kubernetes Service (AKS) cluster with workload identity enabled
- Azure Firewall deployed and configured
- Azure Managed Identity with permissions to modify the Azure Firewall
- `kubectl` configured to access your cluster
- Docker registry for container images (or use the provided ACR)

## Setup

### 1. Configure Azure Workload Identity

Create a managed identity and assign it the necessary permissions:

```powershell
# Create managed identity
az identity create \
  --name firewall-operator-identity \
  --resource-group <your-resource-group>

# Get the client ID
CLIENT_ID=$(az identity show \
  --name firewall-operator-identity \
  --resource-group <your-resource-group> \
  --query clientId -o tsv)

# Assign Network Contributor role to the firewall
az role assignment create \
  --assignee $CLIENT_ID \
  --role "Network Contributor" \
  --scope /subscriptions/<subscription-id>/resourceGroups/<firewall-resource-group>/providers/Microsoft.Network/azureFirewalls/<firewall-name>

# Establish federated identity credential
az identity federated-credential create \
  --name firewall-operator-federated \
  --identity-name firewall-operator-identity \
  --resource-group <your-resource-group> \
  --issuer $(az aks show -n <aks-cluster-name> -g <aks-resource-group> --query "oidcIssuerProfile.issuerUrl" -o tsv) \
  --subject system:serviceaccount:firewall-sync:firewall-operator
```

### 2. Deploy the Operator

Update the `aks/operator.yaml` file with your values:

```yaml
# Update the service account annotation
annotations:
  azure.workload.identity/client-id: "<your-managed-identity-client-id>"

# Update the environment variables
env:
- name: AZURE_SUBSCRIPTION_ID
  value: "<your-subscription-id>"
- name: AZURE_RESOURCE_GROUP
  value: "<firewall-resource-group>"
- name: AZURE_FIREWALL_NAME
  value: "<firewall-name>"
```

Deploy the operator:

```powershell
kubectl apply -f aks/operator.yaml
```

Verify the operator is running:

```powershell
kubectl get pods -n firewall-sync
kubectl logs -n firewall-sync deployment/firewall-operator
```

## Usage

### Creating a Service with Firewall Exposure

To expose a service through Azure Firewall, add the `azure.firewall/public-ip` annotation to your LoadBalancer service:

```yaml
apiVersion: v1
kind: Service
metadata:
  name: my-service
  namespace: default
  annotations:
    service.beta.kubernetes.io/azure-load-balancer-internal: "true"
    azure.firewall/public-ip: "40.70.12.174"  # Replace with your Azure Firewall public IP
spec:
  type: LoadBalancer
  ports:
  - port: 80
    protocol: TCP
    targetPort: 80
  selector:
    app: my-app
```

**Important**: 
- The service must be of type `LoadBalancer`
- It's recommended to use an internal LoadBalancer (`service.beta.kubernetes.io/azure-load-balancer-internal: "true"`)
- The `azure.firewall/public-ip` value should be one of your Azure Firewall's public IP addresses

### Example: Nginx Service

See `aks/workload.yaml` for a complete example:

```powershell
kubectl apply -f aks/workload.yaml
```

This creates:
1. An nginx deployment
2. An internal LoadBalancer service with the firewall annotation
3. The operator automatically creates DNAT rules on the firewall

Once deployed, you can access the service via:
```powershell
curl http://<azure-firewall-public-ip>
```

### Removing Firewall Exposure

To stop exposing a service through the firewall, either:
1. Delete the service: `kubectl delete service <service-name>`
2. Remove the `azure.firewall/public-ip` annotation

The operator will automatically remove the corresponding DNAT rules from the Azure Firewall.

## RBAC Permissions

The operator requires the following Kubernetes permissions:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: firewall-operator
rules:
- apiGroups: [""]
  resources: ["services"]
  verbs: ["get", "list", "watch"]
```

The service account is bound to this ClusterRole, allowing it to:
- **get**: Retrieve individual service details
- **list**: List all services across namespaces
- **watch**: Monitor service events in real-time

## Configuration

The operator is configured via environment variables:

| Variable | Description | Example |
|----------|-------------|---------|
| `AZURE_SUBSCRIPTION_ID` | Azure subscription containing the firewall | `24ef1668-95f3-4c77-adf2-2d023271a3e1` |
| `AZURE_RESOURCE_GROUP` | Resource group containing the firewall | `rg-aks-firewall-dnat` |
| `AZURE_FIREWALL_NAME` | Name of the Azure Firewall | `firewall` |

## Building and Deploying

### Build the Docker Image

```powershell
cd operator
docker build -t <your-registry>/firewallsync:latest .
docker push <your-registry>/firewallsync:latest
```

### Update Deployment Image

Update the image reference in `aks/operator.yaml`:

```yaml
containers:
- name: firewallsync
  image: <your-registry>/firewallsync:latest
```

## Monitoring

View operator logs:

```powershell
kubectl logs -n firewall-sync deployment/firewall-operator -f
```

Check for errors:

```powershell
kubectl logs -n firewall-sync deployment/firewall-operator | grep ERROR
```

## Troubleshooting

### Operator not creating DNAT rules

1. **Check operator logs** for authentication or permission errors
2. **Verify the service has a LoadBalancer IP**:
   ```bash
   kubectl get svc <service-name> -o jsonpath='{.status.loadBalancer.ingress[0].ip}'
   ```
3. **Verify the annotation is present**:
   ```bash
   kubectl get svc <service-name> -o yaml | grep azure.firewall
   ```
4. **Check Azure Firewall permissions** - ensure the managed identity has Network Contributor role

### Workload Identity issues

1. **Verify federated credential is configured**:
   ```bash
   az identity federated-credential list \
     --identity-name firewall-operator-identity \
     --resource-group <your-resource-group>
   ```
2. **Check pod has the required label**:
   ```bash
   kubectl get pod -n firewall-sync -l app=firewall-operator -o yaml | grep azure.workload.identity
   ```

### Service not accessible from internet

1. **Verify DNAT rule exists in Azure Firewall**:
   ```bash
   az network firewall nat-rule list \
     --firewall-name <firewall-name> \
     --resource-group <resource-group> \
     --collection-name K8sServiceDNAT
   ```
2. **Check Network Security Group (NSG) rules** allow traffic to the firewall public IP
3. **Verify the LoadBalancer is accessible from within the cluster**

## Security Considerations

- The operator uses Azure Workload Identity for secure authentication (no secrets required)
- The operator only needs read access to Kubernetes services
- The managed identity should have minimal permissions (Network Contributor on the firewall only)
- Consider using namespace-scoped operators for multi-tenant scenarios
- DNAT rules allow traffic from any source (`*`) by default - modify the code for IP restrictions