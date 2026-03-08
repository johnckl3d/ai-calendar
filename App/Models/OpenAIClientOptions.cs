namespace CpmDemoApp.Models
{
    public class OpenAIClientOptions
    {
        public string Endpoint { get; set; }

        public string Key { get; set; }

        public string DeploymentName { get; set; }

        public string AgentId { get; set; }

        public string AgentVersion { get; set; }
    }
}