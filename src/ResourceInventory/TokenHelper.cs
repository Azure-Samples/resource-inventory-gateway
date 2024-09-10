namespace ResourceInventory;

public static class TokenHelper
{
    private static string? s_cachedToken;
    private static DateTimeOffset s_tokenExpiry = DateTimeOffset.MinValue;

    public static async Task<string> GetAccessToken(ILogger? log = null)
    {
        // Attempt to retrieve the Managed Identity Client ID from environment variables
        var managedIdentityClientId = Environment.GetEnvironmentVariable("MANAGED_IDENTITY_CLIENT_ID");

        try
        {
            if (!string.IsNullOrEmpty(managedIdentityClientId))
            {
                return await GetTokenUsingManagedIdentity(managedIdentityClientId, log);
            }
            else
            {
                return await GetTokenUsingDefaultCredentials(log);
            }
        }
        catch (Exception ex)
        {
            log?.LogError($"Managed identity not found or error occurred: {ex.Message}. Falling back to default credentials.");
            return await GetTokenUsingDefaultCredentials(log);
        }
    }

    private static async Task<string> GetTokenUsingManagedIdentity(string clientId, ILogger? log = null)
    {
        if (IsTokenValid())
        {
            log?.LogInformation("Returning cached token.");
            return s_cachedToken!;
        }

        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = clientId
        });

        return await GetTokenAsync(credential, log);
    }

    private static async Task<string> GetTokenUsingDefaultCredentials(ILogger? log = null)
    {
        if (IsTokenValid())
        {
            log?.LogInformation("Returning cached token.");
            return s_cachedToken!;
        }

        var credential = new DefaultAzureCredential();
        return await GetTokenAsync(credential, log);
    }

    private static bool IsTokenValid()
    {
        return s_cachedToken != null && DateTimeOffset.UtcNow < s_tokenExpiry;
    }

    private static async Task<string> GetTokenAsync(TokenCredential credential, ILogger? log = null)
    {
        try
        {
            var tokenRequestContext = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
            var accessToken = await credential.GetTokenAsync(tokenRequestContext, CancellationToken.None);

            s_cachedToken = accessToken.Token;
            s_tokenExpiry = accessToken.ExpiresOn;

            log?.LogInformation("Successfully obtained and cached new token.");
            return s_cachedToken;
        }
        catch (Exception ex)
        {
            log?.LogError($"Failed to acquire token: {ex.Message}");
            throw;
        }
    }

    public static void ClearCache()
    {
        s_cachedToken = null;
        s_tokenExpiry = DateTimeOffset.MinValue;
    }
}
