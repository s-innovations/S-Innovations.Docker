using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Azure.Management.Resources.Models;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.File;
using Newtonsoft.Json.Linq;
using Renci.SshNet;
using Renci.SshNet.Common;
using SInnovations.Docker.ResourceManager;
using SInnovations.Docker.SwarmManager.Logging;

namespace SInnovations.Docker.SwarmManager
{
    public class SwarmManagerOptions
    {
        public string AdminUserName { get; internal set; }
        public string ManagementHost { get; internal set; }
        public int ManageNatPortStart { get; internal set; }
        public string NodesHost { get; internal set; }
        public int NodesNatPortStart { get; internal set; }
        public PrivateKeyFile PrivateKeyFile { get; set; }
        public string ResourceGroupname { get; internal set; }
        public string StorageAccountKey { get; internal set; }
        public string StorageAccountName { get; internal set; }
    }
    public class VolumeModel
    {
        public ShareReference Share { get; set; }
        public string LocalName { get; set; }
    }
    public class RunOptions
    {
        public int? Memory { get; set; }
    }
    public class RunReference
    {
        private static ILog Logger = LogProvider.GetCurrentClassLogger();

        public Guid Key { get; set; }
        private SshClient _client;
        private TaskCompletionSource<string> _tsc;
        private string _command;
        public RunOptions Options { get; private set; }
        private SwarmManager _manager;
        private Task _runner;

        internal RunReference(SwarmManager manager, SshClient client, string command, RunOptions options)
        {
            Key = manager.CreateGuid(client.ConnectionInfo.Host, client.ConnectionInfo.Port.ToString(), command);
            _tsc = new TaskCompletionSource<string>();
            _client = client;
            _command = command;
            Options = options;
            _manager = manager;

            Run();

        }
        internal void RunIfNotRunning()
        {
            if (_runner == null)
                Run();
        }
        internal void Run()
        {
            Logger.Info($"{Key} : Run");
            if (_runner != null)
                throw new Exception("Already running");

            if (Options.Memory.HasValue && Options.Memory.Value > _manager.AvaibleMb)
            {
                Logger.Info($"{Key} : EnQueue");
                _manager.EnQueueRun(this);
                return;
            }

            if (Options.Memory.HasValue)
                _manager.UsedMb += Options.Memory.Value;

            _runner = Task.Run(async () =>
            {
                try
                {

                    do
                    {
                        if (!_client.IsConnected)
                        {
                            _client.Connect();
                        }

                        try
                        {

                            var sw = Stopwatch.StartNew();
                            Logger.Info($"[{sw.Elapsed}]{Key} : {_command}");
                            var cmd = _client.RunCommand(_command);                         
                           
                            if (cmd.ExitStatus == 0)
                            {
                                Logger.Info($"[{sw.Elapsed}]{Key} : {cmd.Result}");
                                _tsc.SetResult(cmd.Result);

                            }
                            else
                            {
                                Logger.Error($"[{sw.Elapsed}]{Key} : {cmd.Error}");

                                await _manager.InfoAsync();

                                Logger.Info($"{Key} : ReEnqueue");
                                _runner = null;
                                _manager.EnQueueRun(this);

                            }
                            break;
                        }
                        catch (SshConnectionException ex)
                        {
                            //_tsc.SetException(ex);
                            _client.Disconnect();
                            _client.Connect();
                        }



                    } while (true);
                }
                finally
                {
                   // _runner = null;
                    //   _tsc.TrySetException(new Exception("unkown"));
                }
            });
        }
        public Task Completion { get { return _tsc.Task; } }

        public TaskAwaiter<string> GetAwaiter() { return _tsc.Task.GetAwaiter(); }
    }
    public class ClusterInfo
    {
        public int CPUs { get; set; }
    }
    public class ShareReference
    {
        protected SwarmManager _manager;
        internal ShareReference(SwarmManager manager)
        {
            _manager = manager;
        }
        public Guid Key { get; set; }


        public Task DeleteIfExists()
        {
            return this._manager.DeleteIfExists(this);
        }
    }
    public class SwarmManager : IDisposable
    {
        private static ILog Logger = LogProvider.GetCurrentClassLogger();

        public SwarmManagerOptions Options { get; set; }

        public SshClient Client { get; set; }
        public SwarmManager(SwarmManagerOptions options)
        {
            this.Options = options;

            Client = new SshClient(options.ManagementHost, options.ManageNatPortStart, options.AdminUserName, options.PrivateKeyFile);


            Client.Connect();

        }
        public ShareReference GetComputeShare(string name)
        {
            return GetShare(Options.StorageAccountName, Options.StorageAccountKey, name);
        }
        private string CreateVolume(string share, string localName)
        {
            return string.Format("-v $(docker volume create -d azurefile -o share={0}):{1}", share, localName);
        }
        private Dictionary<Guid, CloudFileDirectory> _shares = new Dictionary<Guid, CloudFileDirectory>();

        public CloudFile GetFile(ShareReference dir, string name)
        {
            var cloudDir = _shares[dir.Key];
            name.TrimStart('/');int idx = -1;
            while((idx = name.IndexOf('/')) > -1)
            {
                cloudDir = cloudDir.GetDirectoryReference(name.Substring(0, idx)); //"abd/"
                cloudDir.CreateIfNotExists();
                name = name.Substring(idx+1);
            }

            return cloudDir.GetFileReference(name);
        }
        public async Task DublicateFileAsync(ShareReference source, ShareReference target, string fileName, string targetFileName = null)
        {
            var file = GetFile(source, fileName);
            var targetFile = GetFile(target, targetFileName ?? fileName);

            if (targetFile.Exists())
                return;

            targetFile.StartCopy(new Uri(file.Uri + file.GetSharedAccessSignature(
                new SharedAccessFilePolicy { Permissions = SharedAccessFilePermissions.Read, SharedAccessExpiryTime = DateTimeOffset.UtcNow.AddDays(1) })));
            var backoff = 1000.0; double retries = 1.0;
            while (targetFile.CopyState.Status != CopyStatus.Success)
            {
                await Task.Delay((int)(backoff * (Math.Pow(2.0, retries++) - 1) / 2));
                targetFile.FetchAttributes();

            }
        }
        public Task<bool> FileExistAsync(ShareReference source, string fileName)
        {
            var file = GetFile(source, fileName);
            return file.ExistsAsync();
        }
        public Task DeleteIfExists(ShareReference share)
        {

            var shareref = _shares[share.Key].Share;
            _shares.Remove(share.Key);
            return shareref.DeleteIfExistsAsync();
        }
        public ShareReference GetShare(string name, string key, string shareName)
        {
            var storageAccount = new CloudStorageAccount(new Microsoft.WindowsAzure.Storage.Auth.StorageCredentials(name, key), true);
            var filesClient = storageAccount.CreateCloudFileClient();
            var share = filesClient.GetShareReference(shareName); share.CreateIfNotExists();
            var guid = CreateGuid(name, shareName);
            var reference = new ShareReference(this) { Key = guid };
            _shares[reference.Key] = share.GetRootDirectoryReference();
            return reference;
        }
        public ShareReference GetShare(string conn, string name)
        {
            var storageAccount = CloudStorageAccount.Parse(conn);
            var filesClient = storageAccount.CreateCloudFileClient();
            var share = filesClient.GetShareReference(name); share.CreateIfNotExists();
            var guid = CreateGuid(storageAccount.Credentials.AccountName, name);
            var reference = new ShareReference(this) { Key = guid };
            _shares[reference.Key] = share.GetRootDirectoryReference();
            return reference;
        }
        public RunReference RunAsync(string image, string command, params VolumeModel[] volumes)
        {
            return RunAsync(image, command, new RunOptions(), volumes);
        }
        public RunReference RunAsync(string image, string command, RunOptions runOptions, params VolumeModel[] volumes)
        {
            var volumeString = string.Join(" ", volumes.Select(v => CreateVolume(_shares[v.Share.Key].Share.Name, v.LocalName)));
            return RunCommand($"docker run --rm {(runOptions.Memory.HasValue ? $"-m {runOptions.Memory.Value}M" : "")} -i {volumeString} {image} {command}", Client, runOptions);
        }
        public Guid CreateGuid(params string[] strings)
        {
            byte[] stringbytes = Encoding.UTF8.GetBytes(string.Join("", strings));
            byte[] hashedBytes = new System.Security.Cryptography
                .SHA1CryptoServiceProvider()
                .ComputeHash(stringbytes);
            Array.Resize(ref hashedBytes, 16);
            return new Guid(hashedBytes);
        }
        public static async Task<SwarmManager> CreateOrUpdateSwarmClusterAsync(ApplicationCredentials options,
            string resouceGroupName, int nodeCount, string adminusername, string pubKey, PrivateKeyFile priKey, string dockerUsername, string dockerPassword, string dockerEmail, string location = "West Europe")
        {

            var rg = await ResourceManagerHelper.CreateResourceGroupIfNotExistAsync(options.SubscriptionId, options.AccessToken, resouceGroupName, location);
            var templateStr = new StreamReader(ResourceManagerHelper.Read("SInnovations.Docker.ResourceManager.swarm.json")).ReadToEnd();
            var deployment = ResourceManagerHelper.CreateTemplateDeploymentAsync(options.SubscriptionId, options.AccessToken, rg.Name, "swarmcluster",
                               templateStr,

                                new JObject(

                                    ResourceManagerHelper.CreateValue("nodeCount", nodeCount),
                                    ResourceManagerHelper.CreateValue("adminUsername", adminusername),
                                    ResourceManagerHelper.CreateValue("sshPublicKey", pubKey),
                                     ResourceManagerHelper.CreateValue("dockerHubUsername", dockerUsername),
                                      ResourceManagerHelper.CreateValue("dockerHubPassword", dockerPassword),
                                       ResourceManagerHelper.CreateValue("dockerHubEmail", dockerEmail)

                                    ).ToString()).GetAwaiter().GetResult();

            var outputs = deployment.Properties.Outputs as JObject;
            return new SwarmManager(new SwarmManagerOptions
            {
                AdminUserName = adminusername,
                ManageNatPortStart = 2200,
                NodesNatPortStart = 2200,
                ManagementHost = $"{outputs["masterHostName"]["value"]}.westeurope.cloudapp.azure.com",
                NodesHost = $"{outputs["nodesHostName"]["value"]}.westeurope.cloudapp.azure.com",
                PrivateKeyFile = priKey,
                StorageAccountName = outputs["storageAccountName"]["value"].ToString(),
                StorageAccountKey = outputs["storageAccountKey"]["value"].ToString(),
                ResourceGroupname = resouceGroupName,
            });
        }


        public int Nodes { get; set; }
        public int Cpus { get; set; }
        public int TotalMb { get; set; }
        public int UsedMb { get; set; }
        public int AvaibleMb { get { return TotalMb - UsedMb; } }

        public async Task<string> InfoAsync()
        {
            var infohost = await RunCommand("docker info", Client, new RunOptions());
            var cpusMatch = Regex.Match(infohost, @".*\nCPUs: (\d+).*");
            Cpus = int.Parse(cpusMatch.Groups[1].Value);
            var nodeMathc = Regex.Match(infohost, @".*\n [\b]Nodes: (\d+).*");
            Nodes = int.Parse(nodeMathc.Groups[1].Value);

            var reservedMemoery = Regex.Matches(infohost, @"\n.*Reserved Memory: (.*?) / (.*?)\n");
            var totalMb = 0;
            var usedMb = 0;
            foreach (Match a in reservedMemoery)
            {
                var used = a.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var total = a.Groups[2].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                totalMb += (int)ParseSize(total);
                usedMb += (int)ParseSize(used);


            }
            TotalMb = totalMb;
            UsedMb = usedMb;
            return infohost;
        }

        private double ParseSize(string[] total)
        {
            switch (total[1])
            {
                case "GiB":
                    return double.Parse(total[0]) * 1024;
                case "MiB":
                    return double.Parse(total[0]);
                case "B":
                    return double.Parse(total[0]) / 1024;
                default:
                    throw new Exception("Unknown size " + total[1]);
            }
        }

        public async Task PullImageOnEachNodeAsync(string image)
        {

            using (var client = new SshClient(Options.ManagementHost, Options.ManageNatPortStart, Options.AdminUserName, Options.PrivateKeyFile))
            {
                client.Connect();


                var infohost = await RunCommand("docker info", client, new RunOptions());
                var cpusMatch = Regex.Match(infohost, @".*\nCPUs: (\d+).*");
                var cpus = int.Parse(cpusMatch.Groups[1].Value);
                var nodeMathc = Regex.Match(infohost, @".*\n [\b]Nodes: (\d+).*");
                var nodes = int.Parse(nodeMathc.Groups[1].Value);

                Parallel.For(Options.NodesNatPortStart, Options.NodesNatPortStart + nodes, (slavePort) =>
               {
                   using (var slaveClient = new SshClient(Options.NodesHost, slavePort, Options.AdminUserName, Options.PrivateKeyFile))
                   {
                       slaveClient.Connect();
                       RunCommand($"docker pull {image}", slaveClient, new RunOptions()).Completion.Wait();

                   }
               });
            }

        }
        private Dictionary<Guid, RunReference> _running = new Dictionary<Guid, RunReference>();
        private Queue<RunReference> _runQueue = new Queue<RunReference>();
        private RunReference RunCommand(string command, SshClient client, RunOptions options)
        {
            var run = new RunReference(this, client, command, options);
            TrackRunning(run);
            return run;
        }
        private System.Timers.Timer _idleCheckTimer;
        private void SetIdleCheckTimer()
        {
            if (_idleCheckTimer == null)
            {
                _idleCheckTimer = new System.Timers.Timer(TimeSpan.FromMinutes(2).TotalMilliseconds);
                _idleCheckTimer.AutoReset = false;
                _idleCheckTimer.Elapsed += new System.Timers.ElapsedEventHandler(OnIdleCheckTimer);
                _idleCheckTimer.Start();
            }
        }
      
        private async void OnIdleCheckTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                await InfoAsync();
                if (!_runQueue.Any())
                {
                    foreach (var run in _running.Values)
                    {
                       // run.RunIfNotRunning();
                    }
                }

            }
            finally
            {
                _idleCheckTimer = null;

                if (_running.Any())
                {
                    SetIdleCheckTimer();
                }
            }
        }
        internal void TrackRunning(RunReference run)
        {
            _running.Add(run.Key, run);
           
            run.Completion.ContinueWith(r =>
            {
                _running.Remove(run.Key);
                if (run.Options.Memory.HasValue)
                    UsedMb -= run.Options.Memory.Value;

                while (_runQueue.Any() &&  (!_runQueue.Peek().Options.Memory.HasValue || _runQueue.Peek().Options.Memory.Value < AvaibleMb))
                {
                    _runQueue.Dequeue().Run();
                 
                }
               

            });

            SetIdleCheckTimer();



        }

        public override string ToString()
        {
            return $"SwarmManager<tasks:{_running.Count}, queued:{_runQueue.Count}, mem<used:{UsedMb}Mb, available:{AvaibleMb}Mb>>";
        }
        internal void EnQueueRun(RunReference run)
        {
            if (!run.Options.Memory.HasValue || run.Options.Memory.Value < AvaibleMb)
            {
                run.Run();
            }
            else {
                _runQueue.Enqueue(run);
            }
        }

        public void Dispose()
        {
            Client.Disconnect();
            Client.Dispose();
        }
    }
}
