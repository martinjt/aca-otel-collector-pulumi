using Pulumi;
using Pulumi.AzureNative.App.V20230401Preview.Inputs;
using Pulumi.AzureNative.App.V20230401Preview;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.Storage.Inputs;
using System.Collections.Generic;
using Pulumi.Command.Local;
using System.IO;
using System.Security.Cryptography;
using System;
using Type = Pulumi.AzureNative.App.V20230401Preview.Type;
using FileShare = Pulumi.AzureNative.Storage.FileShare;

return await Pulumi.Deployment.RunAsync(() =>
{
    // Create an Azure Resource Group
    var resourceGroup = new ResourceGroup("resourceGroup");

    // Create an Azure resource (Storage Account)
    var storageAccount = new StorageAccount("sa", new StorageAccountArgs
    {
        ResourceGroupName = resourceGroup.Name,
        Sku = new SkuArgs
        {
            Name = SkuName.Standard_LRS
        },
        Kind = Kind.StorageV2
    });

    var fileShare = new FileShare("fileShare", new()
    {
        ResourceGroupName = resourceGroup.Name,
        AccountName = storageAccount.Name,
        EnabledProtocols = "SMB",
        ShareName = "config",
    });
    
    var storageAccountKeys = ListStorageAccountKeys.Invoke(new ListStorageAccountKeysInvokeArgs
    {
        ResourceGroupName = resourceGroup.Name,
        AccountName = storageAccount.Name
    });

    string configHash;
    using var fs = new FileStream("../config.yaml", FileMode.Open);
    using var sha256 = SHA256.Create();
        configHash = BitConverter.ToString(sha256.ComputeHash(fs));    

    var configFileUpload = new Command("config-file-upload", new CommandArgs {
        Triggers = new List<object> { configHash },
        Update =  Output.Format(@$"
        az storage file upload -s {fileShare.Name} \
                    --source ../config.yaml \
                    --no-progress \
                    --account-key {storageAccountKeys.Apply(k => k.Keys[0].Value)} \
                    --account-name {storageAccount.Name} > /dev/null"),
        Create = Output.Format(@$"az storage file upload -s {fileShare.Name} \
                    --source ../config.yaml \
                    --no-progress \
                    --account-key {storageAccountKeys.Apply(k => k.Keys[0].Value)} \
                    --account-name {storageAccount.Name} > /dev/null"),
    }, new() { DeletedWith = fileShare });

    var containerAppEnvironment = new ManagedEnvironment("collector-env", new ManagedEnvironmentArgs
    {
        ResourceGroupName = resourceGroup.Name,
        AppLogsConfiguration = new AppLogsConfigurationArgs
        {
            Destination = ""
        },
    });

    var containerAppEnvironmentStorage = new ManagedEnvironmentsStorage("collector-env-storage", new ManagedEnvironmentsStorageArgs
    {
        ResourceGroupName = resourceGroup.Name,
        EnvironmentName = containerAppEnvironment.Name,
        StorageName = "collector-config",
        Properties = new ManagedEnvironmentStoragePropertiesArgs
        {
            AzureFile = new AzureFilePropertiesArgs
            {
                AccessMode = "ReadWrite",
                ShareName = fileShare.Name,
                AccountKey = storageAccountKeys.Apply(k => k.Keys[0].Value),
                AccountName = storageAccount.Name,
            }
        }
    });

    var config = new Config();
    var honeycombApiKeySecret = new SecretArgs
    {
        Name = "honeycomb-api-key",
        Value = config.RequireSecret("HONEYCOMB_API_KEY")

    };

    var collectorApp = new ContainerApp("collector", new ContainerAppArgs
    {
        EnvironmentId = containerAppEnvironment.Id,
        ResourceGroupName = resourceGroup.Name,
        ContainerAppName = "collector",
        Configuration = new ConfigurationArgs
        {
            Ingress = new IngressArgs
            {
                External = true,
                TargetPort = 4318
            },
            Secrets = {
                honeycombApiKeySecret
            },

        },
        Template = new TemplateArgs
        {
            Scale = new ScaleArgs
            {
                MinReplicas = 1,
                MaxReplicas = 1,
            },
            Volumes = {
                new VolumeArgs
                {
                    Name = "config",
                    StorageType = StorageType.AzureFile,
                    StorageName = containerAppEnvironmentStorage.Name,
                }
            },
            Containers = {
                new ContainerArgs
                {
                    Name = "collector",
                    Image = "otel/opentelemetry-collector-contrib:latest",
                    VolumeMounts = {
                        new VolumeMountArgs
                        {
                            VolumeName = "config",
                            MountPath = "/etc/otelcol-contrib",
                        }
                    },
                    Env = {
                        new EnvironmentVarArgs {
                            SecretRef = honeycombApiKeySecret.Name,
                            Name = "HONEYCOMB_API_KEY"
                        },
                        new EnvironmentVarArgs {
                            Name = "CONFIG_FILE_HASH", // used to trigger revision updates
                            Value = configHash
                        }
                    },
                    Probes = {
                        new ContainerAppProbeArgs {
                            HttpGet = new ContainerAppProbeHttpGetArgs {
                                Path = "/",
                                Port = 13133,
                            },
                            Type = Type.Readiness
                        },
                        new ContainerAppProbeArgs {
                            HttpGet = new ContainerAppProbeHttpGetArgs {
                                Path = "/",
                                Port = 13133,
                            },
                            Type = Type.Liveness
                        }

                    }

                }
            },
        }
    }, new() {DependsOn = { configFileUpload } 
    });

    return new Dictionary<string, object?> { 
        { "collector-url", collectorApp } 
    };
});
