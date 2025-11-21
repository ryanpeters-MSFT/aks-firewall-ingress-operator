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

- Azure CLI installed and authenticated
- Docker installed (for building the operator image)
- Azure Container Registry (ACR) or another container registry
- `kubectl` installed

## Setup

### 1. Run the Setup Script

The `aks/setup.ps1` script automates the creation of all Azure resources including AKS cluster, Azure Firewall, managed identity, and federated credentials.

Edit the variables at the top of `aks/setup.ps1` to match your desired configuration:

```powershell
$group = "rg-aks-firewall-dnat"             # Resource group name
$location = "eastus2"                       # Azure region
$registry = "binarydad"                     # Your ACR name (must exist)
$clusterName = "firewallcluster"            # AKS cluster name
$fwName = "firewall"                        # Azure Firewall name
$fwIdentity = "fwoperator"                  # Managed identity name
```

Run the setup script:

```powershell
cd aks
./setup.ps1
```

The script will output the required values at the end:

- `AZURE_SUBSCRIPTION_ID`
- `AZURE_RESOURCE_GROUP`
- `AZURE_FIREWALL_NAME`
- `Operator Client ID`
- `Firewall Public IP`

You'll use these values in step 3 to configure the operator deployment.

### 2. Build and Push the Operator Image

Edit `operator/docker.ps1` to use your container registry:

```powershell
az acr login -n <your-registry-name>
docker build -f .\Dockerfile . -t <your-registry>.azurecr.io/firewallsync:latest
docker push <your-registry>.azurecr.io/firewallsync:latest
```

Run the docker build script:

```powershell
cd operator
./docker.ps1
```

### 3. Deploy the Operator

Update `aks/operator.yaml` with the values from step 1:

```yaml
# Update the service account annotation with the operator Client ID from step 1
annotations:
  azure.workload.identity/client-id: "<operator-client-id-from-setup-output>"

# Update the environment variables
env:
- name: AZURE_SUBSCRIPTION_ID
  value: "<AZURE_SUBSCRIPTION_ID-from-setup-output>"
- name: AZURE_RESOURCE_GROUP
  value: "<AZURE_RESOURCE_GROUP-from-setup-output>"
- name: AZURE_FIREWALL_NAME
  value: "<AZURE_FIREWALL_NAME-from-setup-output>"

# Update the container image
image: <your-registry>.azurecr.io/firewallsync:latest
```

Deploy the operator:

```powershell
kubectl apply -f aks/operator.yaml
```

This creates:
- Namespace: `firewall-sync`
- ServiceAccount: `firewall-operator` (with workload identity annotation)
- ClusterRole: `firewall-operator` (with permissions to watch services)
- ClusterRoleBinding: Binds the service account to the cluster role
- Deployment: `firewall-operator` (the operator pod)

Verify the operator is running:

```powershell
kubectl get pods -n firewall-sync
kubectl logs -n firewall-sync deployment/firewall-operator -f
```

### 4. Deploy a Sample Workload

The setup script already outputs your Azure Firewall's public IP address. If you need to retrieve it again:

```powershell
az network public-ip show `
  --resource-group <your-resource-group> `
  --name fwpip `
  --query ipAddress `
  --output tsv
```

Two sample workloads are provided for testing:

#### Option A: Nginx on Port 80

Update `aks/workload-nginx.yaml` with your firewall's public IP:

```yaml
annotations:
  service.beta.kubernetes.io/azure-load-balancer-internal: "true"
  azure.firewall/public-ip: "<firewall-public-ip>"
```

Deploy the nginx workload:

```powershell
kubectl apply -f aks/workload-nginx.yaml
```

Wait for the LoadBalancer service to receive an internal IP:

```powershell
kubectl get svc nginx -w
```

Once the service has an `EXTERNAL-IP`, the operator will automatically create DNAT rules. Test access:

```powershell
curl http://<firewall-public-ip>
```

You should see the nginx welcome page!

#### Option B: Echo Server on Port 8080

Update `aks/workload-echo.yaml` with your firewall's public IP:

```yaml
annotations:
  service.beta.kubernetes.io/azure-load-balancer-internal: "true"
  azure.firewall/public-ip: "<firewall-public-ip>"
```

Deploy the echo workload:

```powershell
kubectl apply -f aks/workload-echo.yaml
```

Wait for the LoadBalancer service to receive an internal IP:

```powershell
kubectl get svc echo -w
```

Once the service has an `EXTERNAL-IP`, the operator will automatically create DNAT rules. Test access:

```powershell
curl http://<firewall-public-ip>:8080
```

You should see a JSON response with request details from the echo server!

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
- Use an internal LoadBalancer with the annotation `service.beta.kubernetes.io/azure-load-balancer-internal: "true"`
- The `azure.firewall/public-ip` value must be your Azure Firewall's public IP address

To get your Azure Firewall's public IP:

```powershell
az network public-ip show `
  --resource-group <your-resource-group> `
  --name fwpip `
  --query ipAddress `
  --output tsv
```

### Example Workloads

Two complete examples are provided:

**Nginx on Port 80** (`aks/workload-nginx.yaml`):
```powershell
kubectl apply -f aks/workload-nginx.yaml
curl http://<azure-firewall-public-ip>
```

**Echo Server on Port 8080** (`aks/workload-echo.yaml`):
```powershell
kubectl apply -f aks/workload-echo.yaml
curl http://<azure-firewall-public-ip>:8080
```

Each workload creates:
1. A deployment with the application
2. An internal LoadBalancer service with the firewall annotation
3. The operator automatically creates DNAT rules on the firewall

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

The operator is configured via environment variables set in the `operator.yaml` deployment:

| Variable | Description | Example |
|----------|-------------|---------|
| `AZURE_SUBSCRIPTION_ID` | Azure subscription containing the firewall | `24ef1668-95f3-4c77-adf2-2d023271a3e1` |
| `AZURE_RESOURCE_GROUP` | Resource group containing the firewall | `rg-aks-firewall-dnat` |
| `AZURE_FIREWALL_NAME` | Name of the Azure Firewall | `firewall` |

These values are output by the `setup.ps1` script.

## Monitoring

View operator logs:

```powershell
kubectl logs -n firewall-sync deployment/firewall-operator -f
```

Check for errors:

```powershell
kubectl logs -n firewall-sync deployment/firewall-operator | Select-String ERROR
```

## Troubleshooting

### Operator not creating DNAT rules

1. **Check operator logs** for authentication or permission errors
2. **Verify the service has a LoadBalancer IP**:
   ```powershell
   kubectl get svc <service-name> -o jsonpath='{.status.loadBalancer.ingress[0].ip}'
   ```
3. **Verify the annotation is present**:
   ```powershell
   kubectl get svc <service-name> -o yaml | Select-String azure.firewall
   ```
4. **Check Azure Firewall permissions** - ensure the managed identity has Network Contributor role on the firewall and Reader role on the resource group (assigned by setup.ps1)

### Workload Identity issues

1. **Verify federated credential is configured**:
   ```powershell
   az identity federated-credential list `
     --identity-name <identity-name> `
     --resource-group <your-resource-group>
   ```
2. **Check pod has the required label**:
   ```powershell
   kubectl get pod -n firewall-sync -l app=firewall-operator -o yaml | Select-String azure.workload.identity
   ```

### Service not accessible from internet

1. **Verify DNAT rule exists in Azure Firewall**:
   ```powershell
   az network firewall nat-rule list `
     --firewall-name <firewall-name> `
     --resource-group <resource-group> `
     --collection-name K8sServiceDNAT
   ```
2. **Check Network Security Group (NSG) rules** allow traffic to the firewall public IP
3. **Verify the LoadBalancer is accessible from within the cluster**

## Security Considerations

- The operator uses Azure Workload Identity for secure authentication (no secrets required)
- The operator only needs read access to Kubernetes services
- The managed identity has minimal Azure permissions (configured by setup.ps1):
  - **Network Contributor** role on the Azure Firewall (to manage DNAT rules)
  - **Reader** role on the resource group (to read firewall configuration)
- Consider using namespace-scoped operators for multi-tenant scenarios
- DNAT rules allow traffic from any source (`*`) by default - modify the code for IP restrictions

## Contributing

Contributions are welcome! Please feel free to submit issues or pull requests.

## License

This project is provided as-is for demonstration purposes.