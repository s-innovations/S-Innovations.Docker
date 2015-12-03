using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SInnovations.Docker.ResourceManager
{
    public class ApplicationCredentials
    {
        public string CliendId { get; set; }
        public string ReplyUrl { get; set; }
        public string Secret { get; set; }
        public string SubscriptionId { get; set; }
        public string TenantId { get; set; }
    }
}
