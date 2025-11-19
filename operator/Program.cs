using k8s;
using k8s.Models;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Azure.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<FirewallSyncOperator>();
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});

var host = builder.Build();
await host.RunAsync();

public class FirewallSyncOperator : BackgroundService
{
    private readonly ILogger<FirewallSyncOperator> _logger;
    private readonly IKubernetes _k8sClient;
    private readonly AzureFirewallResource _firewall;
    private const string FirewallIpAnnotation = "azure.firewall/public-ip";
    private const string RuleCollectionName = "K8sServiceDNAT";

    public FirewallSyncOperator(ILogger<FirewallSyncOperator> logger)
    {
        _logger = logger;

        // Initialize Kubernetes client (in-cluster or local kubeconfig)
        var k8sConfig = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _k8sClient = new Kubernetes(k8sConfig);

        // Initialize Azure Firewall client
        var credential = new DefaultAzureCredential();
        var armClient = new ArmClient(credential);

        // Replace with your actual values
        var subscriptionId = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");
        var resourceGroupName = Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP");
        var firewallName = Environment.GetEnvironmentVariable("AZURE_FIREWALL_NAME");

        var firewallResourceId = AzureFirewallResource.CreateResourceIdentifier(
            subscriptionId,
            resourceGroupName,
            firewallName);
        _firewall = armClient.GetAzureFirewallResource(firewallResourceId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Firewall Sync Operator");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WatchServicesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in service watcher, restarting...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task WatchServicesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting watch on LoadBalancer services...");

                var watchTask = _k8sClient.CoreV1.WatchListServiceForAllNamespacesAsync(
                    cancellationToken: cancellationToken);

                await foreach (var (eventType, service) in watchTask.ConfigureAwait(false))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Only process LoadBalancer services
                    if (service.Spec?.Type != "LoadBalancer")
                        continue;

                    _logger.LogInformation($"Service event: {eventType} - {service.Metadata.Name} in {service.Metadata.NamespaceProperty}");

                    try
                    {
                        switch (eventType)
                        {
                            case WatchEventType.Added:
                            case WatchEventType.Modified:
                                await HandleServiceAddedOrModifiedAsync(service);
                                break;
                            case WatchEventType.Deleted:
                                await HandleServiceDeletedAsync(service);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error handling service event for {service.Metadata.Name}");
                    }
                }

                _logger.LogWarning("Watch connection ended, reconnecting...");
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Watch cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watch failed, reconnecting in 5 seconds...");
                await Task.Delay(5000, cancellationToken);
            }
        }
    }


    private async Task HandleServiceAddedOrModifiedAsync(V1Service service)
    {
        // Check if service has LoadBalancer IP assigned
        if (service.Status?.LoadBalancer?.Ingress == null ||
            !service.Status.LoadBalancer.Ingress.Any())
        {
            _logger.LogInformation($"Service {service.Metadata.Name} has no LoadBalancer IP yet, skipping");
            return;
        }

        // Get the firewall public IP from annotation
        if (service.Metadata.Annotations == null ||
            !service.Metadata.Annotations.TryGetValue(FirewallIpAnnotation, out var firewallPublicIp))
        {
            _logger.LogInformation($"Service {service.Metadata.Name} has no firewall annotation, skipping");
            return;
        }

        var loadBalancerIp = service.Status.LoadBalancer.Ingress.First().Ip;

        // Create DNAT rules for each port
        var rules = new List<AzureFirewallNatRule>();
        foreach (var port in service.Spec.Ports ?? Enumerable.Empty<V1ServicePort>())
        {
            var ruleName = $"{service.Metadata.NamespaceProperty}-{service.Metadata.Name}-{port.Port}";

            var protocol = port.Protocol?.ToLowerInvariant() == "udp"
                ? AzureFirewallNetworkRuleProtocol.Udp
                : AzureFirewallNetworkRuleProtocol.Tcp;

            var rule = new AzureFirewallNatRule
            {
                Name = ruleName,
                Description = $"DNAT for K8s service {service.Metadata.NamespaceProperty}/{service.Metadata.Name}",
                SourceAddresses = { "*" },
                DestinationAddresses = { firewallPublicIp },
                DestinationPorts = { port.Port.ToString() },
                Protocols = { protocol },
                TranslatedAddress = loadBalancerIp,
                TranslatedPort = port.Port.ToString()
            };

            rules.Add(rule);
            _logger.LogInformation($"Prepared DNAT rule: {firewallPublicIp}:{port.Port} -> {loadBalancerIp}:{port.Port}");
        }

        await UpdateFirewallRulesAsync(service, rules);
    }

    private async Task HandleServiceDeletedAsync(V1Service service)
    {
        _logger.LogInformation($"Removing DNAT rules for deleted service {service.Metadata.Name}");
        await UpdateFirewallRulesAsync(service, new List<AzureFirewallNatRule>());
    }

    private async Task UpdateFirewallRulesAsync(V1Service service, List<AzureFirewallNatRule> newRules)
    {
        try
        {
            // Get current firewall configuration
            var currentFirewall = await _firewall.GetAsync();
            var firewallData = currentFirewall.Value.Data;

            // Find or create the NAT rule collection
            var natRuleCollection = firewallData.NatRuleCollections
                .FirstOrDefault(c => c.Name == RuleCollectionName);

            if (natRuleCollection == null)
            {
                natRuleCollection = new AzureFirewallNatRuleCollectionData
                {
                    Name = RuleCollectionName,
                    Priority = 100,
                    ActionType = AzureFirewallNatRCActionType.Dnat
                };
                firewallData.NatRuleCollections.Add(natRuleCollection);
            }

            // Remove existing rules for this service
            var servicePrefix = $"{service.Metadata.NamespaceProperty}-{service.Metadata.Name}-";
            var existingRules = natRuleCollection.Rules
                .Where(r => !r.Name.StartsWith(servicePrefix))
                .ToList();

            // Add new rules
            natRuleCollection.Rules.Clear();
            foreach (var rule in existingRules.Concat(newRules))
            {
                natRuleCollection.Rules.Add(rule);
            }

            // Create a new AzureFirewallData instance with updated rules
            var updatedData = new AzureFirewallData
            {
                Location = firewallData.Location,
                Tags = { },
                ApplicationRuleCollections = { },
                NatRuleCollections = { },
                NetworkRuleCollections = { }
            };

            // Copy tags
            foreach (var tag in firewallData.Tags)
            {
                updatedData.Tags.Add(tag);
            }

            // Copy all rule collections
            foreach (var appRule in firewallData.ApplicationRuleCollections)
            {
                updatedData.ApplicationRuleCollections.Add(appRule);
            }

            foreach (var natRule in firewallData.NatRuleCollections)
            {
                updatedData.NatRuleCollections.Add(natRule);
            }

            foreach (var netRule in firewallData.NetworkRuleCollections)
            {
                updatedData.NetworkRuleCollections.Add(netRule);
            }

            // Copy other required properties
            updatedData.IPConfigurations.Clear();
            foreach (var ipConfig in firewallData.IPConfigurations)
            {
                updatedData.IPConfigurations.Add(ipConfig);
            }

            if (firewallData.Sku != null)
            {
                updatedData.Sku = firewallData.Sku;
            }

            // Update the firewall
            _logger.LogInformation("Updating Azure Firewall...");

            var credential = new DefaultAzureCredential();
            var armClient = new ArmClient(credential);
            var subscription = armClient.GetSubscriptionResource(
                new ResourceIdentifier($"/subscriptions/{Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID")}"));
            var resourceGroup = await subscription.GetResourceGroupAsync(
                Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP"));

            var operation = await resourceGroup.Value.GetAzureFirewalls()
                .CreateOrUpdateAsync(
                    Azure.WaitUntil.Completed,
                    Environment.GetEnvironmentVariable("AZURE_FIREWALL_NAME"),
                    updatedData);

            _logger.LogInformation($"Successfully updated firewall for service {service.Metadata.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to update firewall for service {service.Metadata.Name}");
        }
    }


}
