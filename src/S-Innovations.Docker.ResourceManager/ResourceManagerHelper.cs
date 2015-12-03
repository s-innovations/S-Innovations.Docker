using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Azure;
using Microsoft.Azure.Management.Resources;
using Microsoft.Azure.Management.Resources.Models;
using Microsoft.Azure.Subscriptions;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SInnovations.Docker.ResourceManager
{
    // This project can output the Class library as a NuGet Package.
    // To enable this option, right-click on the project and select the Properties menu item. In the Build tab select "Produce outputs on build".
    public class ResourceManagerHelper
    {
      
         public static AuthenticationResult GetAuthorizationHeader(string tenant, string clientId,string secret, string redirectUrl=null)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                ClientCredential cc = new ClientCredential(clientId, secret);
                var context = new AuthenticationContext($"https://login.windows.net/{tenant}", new FileCache());
                var result = context.AcquireToken("https://management.core.windows.net/", cc);
                if (result == null)
                {
                    throw new InvalidOperationException("Failed to obtain the JWT token");
                }
                return result;
            }
            else
            {
                var context = new AuthenticationContext($"https://login.windows.net/{tenant}",new FileCache());
                var result = context.AcquireToken("https://management.core.windows.net/",clientId,new Uri(redirectUrl));
                if (result == null)
                {
                    throw new InvalidOperationException("Failed to obtain the JWT token");
                }
                return result;
            }
        }

        public static AuthenticationResult GetAuthorizationHeader(ApplicationCredentials options)
        {
            return GetAuthorizationHeader(options.TenantId, options.CliendId, options.Secret,options.ReplyUrl);
        }

        public static string test(string clientId, string redirectUri, string tenant=null)
        {
            var authContext = new AuthenticationContext($"https://login.windows.net/{tenant??"common"}", new FileCache());
           var t= authContext.GetAuthorizationRequestURL("https://management.core.windows.net/", clientId, new Uri(redirectUri), UserIdentifier.AnyUser, null);
            
            //    var code = "AAABAAAAiL9Kn2Z27UubvWFPbm0gLfmp1ExBQltdN9obd6rGamy-zGUM4_wBv9xKG8Q-XwQl2CpxGEz-N2LYNovZMcylQ1zR_u7XV5TD-aN2yOp1rjC2mJpzAI2AaiezbUOvHKouTgeAvWEDk3QUd_qhGZTWaVkOzYHFawqmPKXshpYQozsRslmvhr49VoVEgJs7eyF7COBennf6A3aVDBGtAijfouJLo1kKhYhalf3bRR1wdbLApj7GKaYb7oy-Q_6mGLry1rcQMNHg5h4gvRPeYqT7jX3FGmUePjj1-TwKsIylvvC4f8f69D4v_Wp11FsI6WLSH95wJAj6FKDG04ixUSoy6AXujJcWMZbv0AOzZ3X-V_EmMFM6InNrebmA_3awMibHNI62EtpOjpgnb4FjyboFplXhcNMOUio1DwwOu7sa0IFm0UVK1KTTCra6V34k9BiQfCR0bZXpG9fn3RqwaaCJZu4NBttP1oXoryrp6YsxbskOqJCTe-_AeiPMgcm-I24rzU_8x9ZKQ-JM5ACFySXQggq_csTcWG-Kj8-JT4VY4xKFOGBCO5czxn_g0bH3UuUf8DniOxZPZ0EEoDaUhxfraTpXVy9p5o4hXyr65Upt5eYy5LxR1Emdc-Mfho92SsEnimqMXexwtopnHqx0z-pr9OCe5vnZZdyWFbHNyhDeM6ADXvnKbTQy2xW3kaUMFWaaLd3-s4jq6_D4Py3Mawtz5iAA";
        //    authContext.AcquireTokenByAuthorizationCode(code, new Uri(redirectUri), new ClientCredential(clientId, secret));
            var token = authContext.AcquireToken("https://management.core.windows.net/", clientId, new Uri(redirectUri), PromptBehavior.Auto,UserIdentifier.AnyUser);
           
            return token.AccessToken;
        }

        public async static Task ListSubscriptions(string azureToken)
        {
            {
                using (var subscriptionClient = new SubscriptionClient(new TokenCloudCredentials(azureToken)))
                {
                    var subscriptions = await subscriptionClient.Subscriptions.ListAsync();

                    foreach (var subscription in subscriptions.Subscriptions)
                    {
                        
                        Console.WriteLine(JsonConvert.SerializeObject(await subscriptionClient.Subscriptions.GetAsync(subscription.SubscriptionId), Formatting.Indented));
                    }


                }
            }

        }
        private static JObject ReadData(string resourceName)
        {
            var assembly = typeof(ResourceManagerHelper).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (StreamReader reader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(reader))
            {

                return JObject.Load(jsonReader);




            }
        }
        public static Stream Read(string name)
        {
            return typeof(ResourceManagerHelper).Assembly.GetManifestResourceStream(name);
        }
        public static string LoadTemplate(string templatePath, string parameterPath, string variablePath, params string[] parameterNames)
        {
            return LoadTemplates(new[] { templatePath }, parameterPath, variablePath, parameterNames);
        }
        public static string LoadTemplates(string[] templatePaths, string parameterPath, string variablePath, params string[] parameterNames)
        {
            var templates = templatePaths.Select(templatePath=> ReadData(templatePath)).ToArray();
            var template = templates.First();
            if(templates.Skip(1).Any())
            {
                var resources = template["resources"] as JArray;
                foreach(var templateCopy in templates.Skip(1))
                {
                    var resourcesCopy = templateCopy["resources"] as JArray;
                    foreach (var resourceCopy in resourcesCopy)
                        resources.Add(resourceCopy);
                }
            }

            var parameters = ReadData(parameterPath);
            var variables = ReadData(variablePath);
            template["parameters"] = new JObject(parameters.Properties().Where(p => parameterNames.Contains(p.Name)));

            var varProps = variables.Properties().Where(CreateFilter(parameterNames, "parameters")).ToArray();
            varProps = varProps.Where(CreateFilter(varProps.Select(p => p.Name).ToArray(), "variables")).ToArray();


            template["variables"] = new JObject(varProps);

            return template.ToString();

        }

        private static Func<JProperty, bool> CreateFilter(string[] parameterNames,string regexName)
        {
            return p =>
            {
                if (p.Value.Type == JTokenType.String)
                {
                    MatchCollection matches = Regex.Matches(p.Value.ToString(), regexName+ @"\((.*?)\)");
                    if (matches.Count > 0)
                    {
                        foreach (Match match in matches)
                        {
                            var paramName = match.Groups[1].Value.Trim('\'', '"');
                            if (!parameterNames.Contains(paramName))
                                return false;

                        }
                    }
                }
                return true;
            };
        }

        public async static Task<ResourceGroupExtended> CreateResourceGroupIfNotExist(string subscriptionId, string token, string resourceGroupName,string location)
        {
            TokenCloudCredentials credential = new TokenCloudCredentials(subscriptionId, token);
            var resourceGroup = new ResourceGroup { Location = location };
            using (var resourceManagementClient = new ResourceManagementClient(credential))
            {
                var rgResult = await resourceManagementClient.ResourceGroups.CreateOrUpdateAsync(resourceGroupName, resourceGroup);
                return rgResult.ResourceGroup;
            }
        }
        public async static Task DeleteIfExists(string subscriptionId, string token, string resourceGroupName, string location)
        {
            TokenCloudCredentials credential = new TokenCloudCredentials(subscriptionId, token);
            var resourceGroup = new ResourceGroup { Location = location };
            using (var resourceManagementClient = new ResourceManagementClient(credential))
            {
                var rgResult = await resourceManagementClient.ResourceGroups.DeleteAsync(resourceGroupName);
             
            }
        }
        public async static Task DeleteTemplateDeployment(string subscriptionId, string token, string resourceGroup, string deploymentName)
        {
            TokenCloudCredentials credential = new TokenCloudCredentials(subscriptionId, token);
            using (var templateDeploymentClient = new ResourceManagementClient(credential))
            {

              var result=  await templateDeploymentClient.Deployments.DeleteAsync(resourceGroup, deploymentName);

             
            }
        }
        public async static Task<DeploymentExtended> CreateTemplateDeploymentAsync(string subscriptionId, string token, string resourceGroup, string deploymentName, string template, string parameters, bool waitForDeployment =true)
        {
            TokenCloudCredentials credential = new TokenCloudCredentials(subscriptionId, token);

            var deployment = new Deployment();
            deployment.Properties = new DeploymentProperties
            {
                Mode = DeploymentMode.Complete,
                Template = template,
                Parameters = parameters            
            };
            using (var templateDeploymentClient = new ResourceManagementClient(credential))
            {
                var result = await templateDeploymentClient.Deployments.ValidateAsync(resourceGroup, deploymentName, deployment);
                if (!result.IsValid)
                {
                    throw new Exception(result.Error.Message);
                }
                var dpResult = await templateDeploymentClient.Deployments.CreateOrUpdateAsync(resourceGroup, deploymentName, deployment);
                var deploymentResult = dpResult.Deployment;
                
                while ( waitForDeployment && !(deploymentResult.Properties.ProvisioningState == "Failed" || deploymentResult.Properties.ProvisioningState == "Succeeded"))
                {
                    var deploymentResultWrapper = await templateDeploymentClient.Deployments.GetAsync(resourceGroup, dpResult.Deployment.Name);

                    deploymentResult = deploymentResultWrapper.Deployment;
                    await Task.Delay(5000);
                }
                return deploymentResult;
            }

        }
    }
}
