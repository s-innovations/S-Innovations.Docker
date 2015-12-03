using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure;
using Microsoft.Azure.Management.Compute;
using Microsoft.Azure.Management.Resources;
using Microsoft.Azure.Management.Resources.Models;
using Microsoft.Rest;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System.Linq;
using SInnovations.Docker.ResourceManager;
using System.Net.Http;
using System.Threading;
using System.Text.RegularExpressions;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.Azure.Management.Compute.Models;
using Renci.SshNet;
using Newtonsoft.Json;
using System.Text;

namespace SInnovations.Docker.Tests
{
    [TestClass]
    public class DeployMasterNodesTest
    {
        ApplicationCredentials options = new ApplicationCredentials
        {
            CliendId = "dedd37c0-c2c2-40fe-b1db-0b0c21d4b55a",
            TenantId = "008c37b3-087c-4341-8630-55457f6dbfb5",
            ReplyUrl = "https://devops.car2cloud.dk",
            SubscriptionId = "6d8083fe-97c5-4f4d-b5d4-cc4bf001bfcb",
        };
        const string name = "c2c11";
        const string rgName = "demo-docker-" + name;


        [TestMethod]
        public async Task CreateResourceGroup()
        {

            var token = ResourceManagerHelper.GetAuthorizationHeader(options);
            var rg = await ResourceManagerHelper.CreateResourceGroupIfNotExist(options.SubscriptionId, token.AccessToken, rgName, "West Europe");

        }
        private static JObject CreateValue(string value)
        {
            return new JObject(new JProperty("value", value));
        }
        private static JProperty CreateValue(string key, JToken value)
        {
            return new JProperty(key, new JObject(new JProperty("value", value)));
        }
        [TestMethod]
        public async Task CreateInfrastructure()
        {
            var token = ResourceManagerHelper.GetAuthorizationHeader(options);
            var rg = await ResourceManagerHelper.CreateResourceGroupIfNotExist(options.SubscriptionId, token.AccessToken, rgName, "West Europe");

            var templateStr = ResourceManagerHelper.LoadTemplate(
                "SInnovations.Docker.ResourceManager.infrastructure.json",
                 "SInnovations.Docker.ResourceManager.parameters.json",
                  "SInnovations.Docker.ResourceManager.variables.json",
                  "resourceLocation", "vmssName"
                );


            var deployment = await ResourceManagerHelper.CreateTemplateDeploymentAsync(options.SubscriptionId, token.AccessToken, rg.Name, "infrastructure",
            templateStr,

                    new JObject(
                        new JProperty("resourceLocation", CreateValue(rg.Location)),
                        new JProperty("vmssName", CreateValue(name))
                        ).ToString()
            );
            var outputs = JObject.Parse(deployment.Properties.Outputs);
            var storage = new CloudStorageAccount(new StorageCredentials(outputs["storageAccountName"]["value"].ToString(),
                outputs["storageAccountKey"]["value"].ToString()), true);
            var setup = storage.CreateCloudBlobClient().GetContainerReference("setup");
            setup.CreateIfNotExists();
            using (var setupscript = ResourceManagerHelper.Read("SInnovations.Docker.ResourceManager.setup_consul.sh"))
            {
                setup.GetBlockBlobReference("setup_consul.sh").UploadFromStream(setupscript);
            }




        }


        [TestMethod]
        public async Task DeploySwarm()
        {
            var rgName = "demo-swarm-60";

            var token = ResourceManagerHelper.GetAuthorizationHeader(options);
            var rg = await ResourceManagerHelper.CreateResourceGroupIfNotExist(options.SubscriptionId, token.AccessToken, rgName, "West Europe");
            var templateStr = new StreamReader(ResourceManagerHelper.Read("SInnovations.Docker.ResourceManager.swarm.json")).ReadToEnd();
            var deployment = await ResourceManagerHelper.CreateTemplateDeploymentAsync(options.SubscriptionId, token.AccessToken, rg.Name, "masterNodes",
                               templateStr,

                                new JObject(

                                    CreateValue("nodeCount", 6),
                                    CreateValue("adminUsername", "pksorensen"),
                                     new JProperty("sshPublicKey", CreateValue("ssh-rsa AAAAB3NzaC1yc2EAAAABJQAAAQEA9V7aWJA/5UlOGAUaz9BhVmrW0mPrlodO8NNWdku1bh5AwauK/iP05FKmwC16Ou150DCw8SxA1LBAKvRgEzwMj9auXe9mklIx7hgCds2kCuDzSJAZB70a8xNF+Os8LZvk/MSKNdvAj6b0wI60IdJqu8WxF5/wHxTd/u1GFpPM0Y1jPwqJMk21giKZ5gb1q+WWJUqAOKWkxQpqcN39zznZEn9D6QMBshqWidYnL1+L6riGd9GZKKd4roac8QO6suuCZJDqaH162V1qTkRghunzdHr6qarC49FenU7+yweBesKFfR+411BABix54zpESYQiV4s/uXLZ3w90uHSMQROUhw== rsa-key-20151110"))

                                    ).ToString());
            Console.WriteLine(deployment.Properties.Outputs);
        }



        [TestMethod]
        public async Task DeployHitRate()
        {
            int testCount = 5;
            var machineCount = 5;
            var tests = new List<Task>();
            var failed = 0;
            var succeded = 0;
            var vmsssucceded = 0;
            var vmssfailed = 0;
            var dockerversion = "1.0";
            foreach (var i in Enumerable.Range(0, testCount))
            {
                tests.Add(Task.Run(async () =>
                {
                    var output = new StringBuilder();
                    var rgName = "demo-vmss-pks-60" + i;
                    var name = "pks8" + i;
                    var token = ResourceManagerHelper.GetAuthorizationHeader(options);
                    var rg = await ResourceManagerHelper.CreateResourceGroupIfNotExist(options.SubscriptionId, token.AccessToken, rgName, "West Europe");
                    //  var templateStr = new StreamReader(ResourceManagerHelper.Read("SInnovations.Docker.ResourceManager.workingtemplate.json")).ReadToEnd();

                    var cred = new TokenCredentials(token.AccessToken);

                    using (var computeManagementClient = new Microsoft.Azure.Management.Compute.ComputeManagementClient(cred))
                    {
                        computeManagementClient.SubscriptionId = options.SubscriptionId;
                        var create = true;
                        try
                        {
                            var vmss = computeManagementClient.VirtualMachineScaleSets.Get(rg.Name, name);
                            create = false; 
                        }
                        catch (Exception ex)
                        {

                        }

                        if (create)
                        {
                            var templateStr = ResourceManagerHelper.LoadTemplates(
                               new[] { "SInnovations.Docker.ResourceManager.infrastructure.json",
                           "SInnovations.Docker.ResourceManager.workingtemplate1.json"
                               },
                                 "SInnovations.Docker.ResourceManager.parameters.json",
                                 "SInnovations.Docker.ResourceManager.variables.json",
                                 "resourceLocation", "vmSku", "vmssName", "instanceCount", "adminUsername", "adminPassword", "ubuntuOSVersion", "dockerversion"
                               );


                            var deployment = await ResourceManagerHelper.CreateTemplateDeploymentAsync(options.SubscriptionId, token.AccessToken, rg.Name, "masterNodes",
                                templateStr,

                                 new JObject(
                                     CreateValue("resourceLocation", "West Europe"),
                                     CreateValue("vmSku", "Standard_A0"),
                                     CreateValue("instanceCount", machineCount),
                                     CreateValue("adminPassword", "4RQce6Hu"),
                                     CreateValue("ubuntuOSVersion", "15.04"),
                                     CreateValue("vmssName", name),
                                     CreateValue("dockerversion", dockerversion),
                                     CreateValue("adminUsername", "pksorensen")
                                     ).ToString());


                            if (deployment.Properties.ProvisioningState == "Failed")
                            {
                                vmssfailed++;

                            }
                            if (deployment.Properties.ProvisioningState == "Succeeded")
                                vmsssucceded++;


                        }

                        using (var networkClient = new Microsoft.Azure.Management.Network.NetworkManagementClient(cred))
                        {
                            networkClient.SubscriptionId = options.SubscriptionId;
                            var ip = await networkClient.PublicIPAddresses.GetWithHttpMessagesAsync(rg.Name, name + "pip");

                            try
                            {
                                var vms = computeManagementClient.VirtualMachineScaleSetVMs.List(rg.Name, name);
                                var a = computeManagementClient.VirtualMachineScaleSets.GetInstanceView(rg.Name, name);
                                foreach (var vm in vms)
                                {
                                    if (vm.ProvisioningState == "Failed")
                                        failed++;
                                    if (vm.ProvisioningState == "Succeeded")
                                        succeded++;

                                    using (var ssh = new SshClient(ip.Body.IpAddress, 50000 + int.Parse(vm.InstanceId), "pksorensen", "4RQce6Hu"))
                                    {
                                        ssh.Connect();
                                 
                                        output.AppendLine($"### Test {i} - Resource Group: {rg.Name}, VMSS: {name}, Instance: {vm.InstanceId} : {vm.ProvisioningState}");

                                        var v = computeManagementClient.VirtualMachineScaleSetVMs.GetInstanceView(rg.Name, name, vm.InstanceId);
                                        output.AppendLine(JsonConvert.SerializeObject(v.Statuses, Formatting.Indented));
                                        output.AppendLine($"ls /var/log/azure/Microsoft.Azure.Extensions.DockerExtension/*/");
                                        output.AppendLine(ssh.RunCommand($"ls /var/log/azure/Microsoft.Azure.Extensions.DockerExtension/*/").Result);

                                        output.AppendLine($"cat /var/log/azure/Microsoft.Azure.Extensions.DockerExtension/*/docker-extension.log");
                                        output.AppendLine(ssh.RunCommand($"cat /var/log/azure/Microsoft.Azure.Extensions.DockerExtension/*/docker-extension.log").Result);
                                        output.AppendLine($"cat /var/log/azure/Microsoft.Azure.Extensions.DockerExtension/*/CommandExecution.log");
                                        output.AppendLine(ssh.RunCommand($"cat /var/log/azure/Microsoft.Azure.Extensions.DockerExtension/*/CommandExecution.log").Result);

                                        output.AppendLine($"cat /var/log/waagent.log");
                                        output.AppendLine(ssh.RunCommand($"cat /var/log/waagent.log").Result);

                                        output.AppendLine();
                                       
                                    }
                                }
                            }
                            catch (Exception)
                            {

                            }



                        }

                        Console.WriteLine(output.ToString());
                    }

                    await ResourceManagerHelper.DeleteIfExists(options.SubscriptionId, token.AccessToken, rgName, "West Europe");


                }));


            }

            await Task.WhenAll(tests);
            Console.WriteLine($"Succes: {succeded}, Failed: {failed}, vmsssucceded: {vmsssucceded}, vmssfailed:{vmssfailed}");

        }
        [TestMethod]
        public async Task CreateMasterNodes()
        {
            await CreateInfrastructure();
            var token = ResourceManagerHelper.GetAuthorizationHeader(options);
            var rg = await ResourceManagerHelper.CreateResourceGroupIfNotExist(options.SubscriptionId, token.AccessToken, rgName, "West Europe");

            var templateStr = ResourceManagerHelper.LoadTemplates(
               new[] { "SInnovations.Docker.ResourceManager.infrastructure.json",
                       "SInnovations.Docker.ResourceManager.masterNodes.json"
               },
                 "SInnovations.Docker.ResourceManager.parameters.json",
                 "SInnovations.Docker.ResourceManager.variables.json",
                 "resourceLocation", "vmssName", "vmSize", "nodeCount", "adminUsername", "sshPublicKey"
               );


            var deployment = await ResourceManagerHelper.CreateTemplateDeploymentAsync(options.SubscriptionId, token.AccessToken, rg.Name, "masterNodes",
            templateStr,

                    new JObject(
                        new JProperty("resourceLocation", CreateValue(rg.Location)),
                        new JProperty("vmssName", CreateValue(name)),
                        new JProperty("adminUsername", CreateValue("pksorensen")),
                        new JProperty("sshPublicKey", CreateValue("ssh-rsa AAAAB3NzaC1yc2EAAAABJQAAAQEA9V7aWJA/5UlOGAUaz9BhVmrW0mPrlodO8NNWdku1bh5AwauK/iP05FKmwC16Ou150DCw8SxA1LBAKvRgEzwMj9auXe9mklIx7hgCds2kCuDzSJAZB70a8xNF+Os8LZvk/MSKNdvAj6b0wI60IdJqu8WxF5/wHxTd/u1GFpPM0Y1jPwqJMk21giKZ5gb1q+WWJUqAOKWkxQpqcN39zznZEn9D6QMBshqWidYnL1+L6riGd9GZKKd4roac8QO6suuCZJDqaH162V1qTkRghunzdHr6qarC49FenU7+yweBesKFfR+411BABix54zpESYQiV4s/uXLZ3w90uHSMQROUhw== rsa-key-20151110"))
                        ).ToString()
            );
        }
        public class vvssD : DelegatingHandler
        {

            protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Console.WriteLine(request.RequestUri.ToString());
                ///subscriptions/6d8083fe-97c5-4f4d-b5d4-cc4bf001bfcb/resourceGroups/demo-vmss-333/providers/Microsoft.Compute/virtualMachines//extensions/DockerExtension
                if (request.RequestUri.AbsoluteUri.Contains("extensions/"))
                {

                    request.RequestUri = new Uri(Regex.Replace(request.RequestUri.AbsoluteUri, "providers/Microsoft.Compute/virtualMachines/(.*?)/extensions/DockerExtension", m =>
                     {
                         var va = m.Value;
                         var vmssName = m.Groups[1].Value.Split('_').First();
                         var instanceName = m.Groups[1].Value.Split('_').Last();
                         return $"providers/Microsoft.Compute/virtualMachineScaleSets/{vmssName}/virtualMachines/{instanceName}/extensions/DockerExtension";
                     }));
                    Console.WriteLine(request.RequestUri.ToString());
                }
                Console.WriteLine();

                var a = await base.SendAsync(request, cancellationToken);
                return a;
            }
        }
        [TestMethod]
        public async Task ListResources()
        {
            var token = ResourceManagerHelper.GetAuthorizationHeader(options);
            ///subscriptions/6d8083fe-97c5-4f4d-b5d4-cc4bf001bfcb/resourceGroups/demo-docker-hello2/providers/Microsoft.Compute/virtualMachineScaleSets/c2c2masters/virtualMachines/0/networkInterfaces/c2c2nic
            ///subscriptions/6d8083fe-97c5-4f4d-b5d4-cc4bf001bfcb/resourceGroups/DEMO-DOCKER-HELLO2/providers/Microsoft.Compute/virtualMachineScaleSets/c2c2masters/virtualMachines/0
            ///subscriptions/6d8083fe-97c5-4f4d-b5d4-cc4bf001bfcb/resourceGroups/DEMO-DOCKER-HELLO2/providers/Microsoft.Compute/virtualMachineScaleSets/c2c2masters

            TokenCloudCredentials credential = new TokenCloudCredentials(options.SubscriptionId, token.AccessToken);
            var cred = new TokenCredentials(token.AccessToken);

            using (var computeManagementClient = new Microsoft.Azure.Management.Compute.ComputeManagementClient(cred))
            {
                computeManagementClient.SubscriptionId = options.SubscriptionId;
                var rgName_ = rgName;
                var resourceGroup = await ResourceManagerHelper.CreateResourceGroupIfNotExist(options.SubscriptionId, token.AccessToken, rgName_, "West Europe");

                var test = await computeManagementClient.VirtualMachineScaleSets.ListAllWithHttpMessagesAsync();
                //  resourceManagementClient.VirtualMachineExtensions.c

                var vms = computeManagementClient.VirtualMachineScaleSetVMs.List(rgName_, name + "masters");
                var a = computeManagementClient.VirtualMachineScaleSets.GetInstanceView(rgName_, name + "masters");

                var v = computeManagementClient.VirtualMachineScaleSetVMs.GetInstanceView(rgName_, name + "masters", "0");



                var updated = computeManagementClient.VirtualMachineScaleSets.CreateOrUpdate(rgName_, name + "masters",
                     new VirtualMachineScaleSet
                     {
                         Location = resourceGroup.Location,
                         VirtualMachineProfile = new VirtualMachineScaleSetVMProfile
                         {

                             ExtensionProfile = new VirtualMachineScaleSetExtensionProfile
                             {
                                 Extensions = new List<VirtualMachineScaleSetExtension>()
                                    {
                                       new VirtualMachineScaleSetExtension
                                       {
                                            AutoUpgradeMinorVersion = true,
                                             Name = "DockerExtension",
                                            Publisher = "Microsoft.Azure.Extensions",
                                             TypeHandlerVersion = "1.0",
                                           VirtualMachineScaleSetExtensionType ="DockerExtension"
                                       }
                                    }
                             }
                         }

                     });
                computeManagementClient.VirtualMachineScaleSets.UpdateInstances(rgName_, name + "masters", new string[] { "0" });


                //    resourceManagementClient.VirtualMachineScaleSetVMs
                ///subscriptions/6d8083fe-97c5-4f4d-b5d4-cc4bf001bfcb/resourceGroups/demo-vmss-343/providers/Microsoft.Compute/virtualMachineScaleSets/test324/virtualMachines/0/networkInterfaces/test324nic

                foreach (var vm in vms)
                {
                    var result = computeManagementClient.VirtualMachineExtensions.CreateOrUpdate(
                        rgName_, vm.Name, "DockerExtension",
                        new Microsoft.Azure.Management.Compute.Models.VirtualMachineExtension
                        {
                            Publisher = "Microsoft.Azure.Extensions",
                            TypeHandlerVersion = "1.0",
                            VirtualMachineExtensionType = "DockerExtension",
                            Settings = new JObject(),
                            Location = resourceGroup.Location
                        });
                }

            }
            using (var networkClient = new Microsoft.Azure.Management.Network.NetworkManagementClient(cred))
            {
                //   networkClient.SubscriptionId = options.SubscriptionId;
                //    var test = await networkClient.NetworkInterfaces.GetVirtualMachineScaleSetNetworkInterfaceWithHttpMessagesAsync("demo-vmss-343", "test324", "0", "test324nic");
            }
        }
    }
}
///subscriptions/6d8083fe-97c5-4f4d-b5d4-cc4bf001bfcb/resourceGroups/demo-vmss-343/providers/Microsoft.Compute/virtualMachineScaleSets/test324/virtualMachines/0/networkInterfaces/test324nic
/// Microsoft.Network/networkInterfaces