using Amazon.BedrockRuntime;
using Amazon.Runtime.CredentialManagement;

namespace Practical.AmazonBedrock;

public class AmazonOptions
{
    public string ProfileName { get; set; }

    public string AccessKeyID { get; set; }

    public string SecretAccessKey { get; set; }

    public string SessionToken { get; set; }

    public string RegionEndpoint { get; set; }

    public string ModelId { get; set; }

    public string AnthropicVersion { get; set; }

    public int MaxTokens { get; set; }

    public decimal Temperature { get; set; }

    public AmazonBedrockRuntimeClient CreateAmazonBedrockRuntimeClient()
    {
        var regionEndpoint = global::Amazon.RegionEndpoint.GetBySystemName(RegionEndpoint);

        if (!string.IsNullOrEmpty(ProfileName))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(ProfileName, out var credentials))
            {
                return new AmazonBedrockRuntimeClient(credentials, regionEndpoint);
            }

            throw new Exception("Unable to load credentials from SSO profile.");

        }
        else if (!string.IsNullOrEmpty(AccessKeyID))
        {
            if (!string.IsNullOrEmpty(SessionToken))
            {
                return new AmazonBedrockRuntimeClient(AccessKeyID, SecretAccessKey, SessionToken, regionEndpoint);
            }

            return new AmazonBedrockRuntimeClient(AccessKeyID, SecretAccessKey, regionEndpoint);
        }

        return new AmazonBedrockRuntimeClient(regionEndpoint);
    }
}
