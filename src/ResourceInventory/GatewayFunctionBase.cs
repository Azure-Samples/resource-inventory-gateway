namespace ResourceInventory;

/// <summary>
/// Abstract base class providing common functionality for gateway functions interacting with ARM & Cost APIs.
/// Implements the IGatewayFunction interface and provides a default implementation for merging results.
/// </summary>
public abstract class GatewayFunctionBase : IGatewayFunction
{
    /// <summary>
    /// Retrieves an access token using the TokenHelper utility.
    /// </summary>
    /// <param name="log">The logger instance for logging access token retrieval process.</param>
    /// <returns>Returns the access token as a string.</returns>
    protected async Task<string> GetAccessTokenAsync(ILogger log)
    {
        return await TokenHelper.GetAccessToken(log);
    }

    /// <summary>
    /// Default implementation for merging multiple JSON responses.
    /// If the root element contains a "value" array, its contents are merged; otherwise, the root element is merged directly.
    /// Can be overridden by derived classes if necessary.
    /// </summary>
    /// <param name="jsonResponses">The list of JSON responses to merge.</param>
    /// <returns>Returns a single JSON string with all merged responses.</returns>
    public virtual string MergeResults(List<string> jsonResponses)
    {
        var allResults = new List<JsonElement>();

        foreach (var jsonResponse in jsonResponses)
        {
            var jsonDocument = JsonDocument.Parse(jsonResponse);
            var rootElement = jsonDocument.RootElement;

            // Check if the root element contains a "value" property
            if (rootElement.TryGetProperty("value", out var valueElement))
            {
                // Add the contents of the "value" array to the results
                allResults.AddRange(valueElement.EnumerateArray());
            }
            else
            {
                // If there's no "value" array, add the root element itself
                allResults.Add(rootElement);
            }
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(new { value = allResults }, options);
    }

    /// <summary>
    /// Validates the inputs for the gateway function.
    /// Derived classes must implement this method to perform custom input validation.
    /// </summary>
    /// <param name="req">The HTTP request containing input parameters.</param>
    /// <param name="validationError">The output validation error message, if the input is invalid.</param>
    /// <returns>Returns true if the inputs are valid, otherwise false.</returns>
    public abstract bool ValidateInputs(HttpRequest req, out string validationError);

    /// <summary>
    /// Constructs the request URL by replacing placeholders in the base URL with the provided identifier.
    /// Derived classes must implement this method for custom URL construction logic.
    /// </summary>
    /// <param name="baseUrl">The base ARM route containing placeholders (e.g., $subscriptionId).</param>
    /// <param name="identifier">The value to replace the placeholders with.</param>
    /// <returns>Returns the fully constructed URL with placeholders replaced.</returns>
    public abstract string BuildRequestUrl(string baseUrl, string identifier);

    /// <summary>
    /// Executes multiple ARM API requests in parallel based on the resource IDs provided.
    /// Derived classes must implement this method to handle fan-out of API calls.
    /// </summary>
    /// <param name="armRoute">The ARM route template for the requests.</param>
    /// <param name="resourceIds">A list of resource IDs for which the API requests will be executed.</param>
    /// <param name="accessToken">The access token for authenticating the ARM API requests.</param>
    /// <param name="log">The logger instance for logging the execution process.</param>
    /// <returns>Returns a list of JSON responses from the ARM API calls.</returns>
    public abstract Task<List<string>> ExecuteFanOutAsync(string armRoute, List<string> resourceIds, string accessToken, ILogger log);
}
