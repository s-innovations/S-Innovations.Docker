using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Azure;
using Microsoft.Azure.ActiveDirectory.GraphClient;
using Microsoft.Azure.Management.KeyVault;
using Microsoft.Azure.Management.Resources;
using Microsoft.Azure.Management.Resources.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SInnovations.Docker.ResourceManager;

using System.Linq;
namespace SInnovations.Docker.Tests
{
    [TestClass]
    public class UnitTest1
    {
        private void ClearCookies()
        {
            NativeMethods.InternetSetOption(IntPtr.Zero, NativeMethods.INTERNET_OPTION_END_BROWSER_SESSION, IntPtr.Zero, 0);
        }

        private static class NativeMethods
        {
            internal const int INTERNET_OPTION_END_BROWSER_SESSION = 42;

            [DllImport("wininet.dll", SetLastError = true)]
            internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer,
                int lpdwBufferLength);
        }
        [TestMethod]
        public async Task TestMethod1()
        {
            ClearCookies();

            var clientid = "fb4fbba5-6fac-404e-b7b6-d7bf4b56a7ea";
            var secret = "random1234";
            var tenantId = "008c37b3-087c-4341-8630-55457f6dbfb5";
            var replyUrl = "https://docker.car2cloud.dk";
            var tokenResult = ResourceManagerHelper.GetAuthorizationHeader(tenantId, clientid, secret);
            var token = tokenResult.AccessToken;

            await ResourceManagerHelper.ListSubscriptions(token);

            var subscriptionid = "6d8083fe-97c5-4f4d-b5d4-cc4bf001bfcb";
            var resourceGroupName = "docker-rg-test";
            var vaultName = "blabla";
            var location = "West Europe";
            var rg = await ResourceManagerHelper.CreateResourceGroupIfNotExist(subscriptionid, token, resourceGroupName, location);

            var authContext = new AuthenticationContext($"https://login.windows.net/{tenantId}");
            var graphtoken = authContext.AcquireToken("https://graph.windows.net/", new ClientCredential(clientid, secret));
            var graph = new ActiveDirectoryClient(new Uri("https://graph.windows.net/" + tenantId), () => Task.FromResult(graphtoken.AccessToken));
            var principals = await graph.ServicePrincipals.Where(p => p.AppId == clientid).ExecuteSingleAsync();


            //await CreateUsingManagementLibraries(clientid, secret, tenantId, token, subscriptionid, resourceGroupName, vaultName, rg);

            var keyVault = new JObject(
                new JProperty("$schema", "https://schema.management.azure.com/schemas/2015-01-01/deploymentTemplate.json#"),
                new JProperty("contentVersion", "1.0.0.0"),
                new JProperty("parameters", new JObject()),
                new JProperty("resources", new JArray(
                    new JObject(
                        new JProperty("type", "Microsoft.KeyVault/vaults"),
                        new JProperty("name", "blabla1"),
                        new JProperty("apiVersion", "2015-06-01"),
                        new JProperty("location", rg.Location),
                        new JProperty("properties",
                            new JObject(
                                new JProperty("enabledForDeployment", true),
                                new JProperty("enabledForDiskEncryption", true),
                                new JProperty("sku", new JObject(new JProperty("name", "Premium"), new JProperty("family", "A"))),
                                new JProperty("accessPolicies", new JArray(
                                    new JObject(
                                        new JProperty("tenantId", tenantId),
                                        new JProperty("objectId", principals.ObjectId),
                                        new JProperty("permissions",
                                            new JObject(
                                                new JProperty("secrets", new JArray("all")),
                                                new JProperty("keys", new JArray("all"))
                                            )
                                        )
                                    ))),
                                new JProperty("tenantId", tenantId)
                            )
                        )
                    )
                ))
             );




            var result = await ResourceManagerHelper.CreateTemplateDeploymentAsync(subscriptionid, token, resourceGroupName, "test", keyVault.ToString(), null);

            await Task.Delay(10000);


            //await ResourceManagerHelper.DeleteTemplateDeployment(subscriptionid, token, resourceGroupName, "test");

            using (var resourceManagementClient = new ResourceManagementClient(new TokenCloudCredentials(subscriptionid, token)))
            {
                resourceManagementClient.ResourceGroups.Delete(resourceGroupName);
                var vaults = resourceManagementClient.Resources.List(new ResourceListParameters { ResourceType = "Microsoft.KeyVault/vaults", ResourceGroupName = resourceGroupName });
                foreach (var vault in vaults.Resources)
                {
                    //                    var a = resourceManagementClient.ResourceProviderOperationDetails.List(new ResourceIdentity(vault.Name, vault.Type, "2015-06-01"));
                    var resourceProvider = resourceManagementClient.Providers.Get(vault.Type.Substring(0, vault.Type.IndexOf("/")));
                    var a = resourceProvider.Provider.ResourceTypes.Single(k => k.Name == vault.Type.Substring(vault.Type.IndexOf("/") + 1));
                    var version = a.ApiVersions.First();
                    resourceManagementClient.Resources.Delete(resourceGroupName, new ResourceIdentity(vault.Name, vault.Type, version));
                }
            }

            //   CreateTemplateDeployment

            //  Console.WriteLine(ResourceManagerHelper.test("dedd37c0-c2c2-40fe-b1db-0b0c21d4b55a", "https://devlops.car2cloud.dk", tenant));
            //  Console.WriteLine(test);
        }

        private static async Task CreateUsingManagementLibraries(string clientid, string secret, string tenantId, string token, string subscriptionid, string resourceGroupName, string vaultName, ResourceGroup rg)
        {
            using (var client = new KeyVaultManagementClient(new TokenCloudCredentials(subscriptionid, token)))
            {
                using (var resourceManagementClient = new ResourceManagementClient(client.Credentials))
                {
                    var authContext = new AuthenticationContext($"https://login.windows.net/{tenantId}");
                    var graphtoken = authContext.AcquireToken("https://graph.windows.net/", new ClientCredential(clientid, secret));
                    var graph = new ActiveDirectoryClient(new Uri("https://graph.windows.net/" + tenantId), () => Task.FromResult(graphtoken.AccessToken));
                    var principals = await graph.ServicePrincipals.Where(p => p.AppId == clientid).ExecuteSingleAsync();


                    resourceManagementClient.Providers.Register("Microsoft.KeyVault");
                    var vaults = resourceManagementClient.Resources.List(new ResourceListParameters { ResourceType = "Microsoft.KeyVault/vaults", ResourceGroupName = resourceGroupName });

                    Console.WriteLine(JsonConvert.SerializeObject(
                                    client.Vaults.CreateOrUpdate(resourceGroupName, vaultName, new VaultCreateOrUpdateParameters
                                    {
                                        Properties = new VaultProperties
                                        {
                                            EnabledForDeployment = true,
                                            Sku = new Sku { Name = "Premium", Family = "A" },
                                            AccessPolicies = new List<AccessPolicyEntry>{
                                            new AccessPolicyEntry{
                                                 TenantId = Guid.Parse(tenantId),
                                                 ObjectId = Guid.Parse(principals.ObjectId),
                                                 PermissionsToSecrets=new []{"all"},
                                                  PermissionsToKeys = new []{"all"}
                                            },
                                            },
                                            TenantId = Guid.Parse(tenantId)
                                        },
                                        Location = rg.Location,

                                    }),
                                 Formatting.Indented));

                }
            }
        }
    }
}
