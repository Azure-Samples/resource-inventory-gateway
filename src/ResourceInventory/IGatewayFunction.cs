namespace ResourceInventory;

/// <summary>
/// Interface defining the operations for gateway functions interacting with ARM APIs.
/// </summary>
public interface IGatewayFunction
{
    /// <summary>
    /// Validates the inputs for the gateway function.
    /// </summary>
    /// <param name="req">The HTTP request containing input parameters.</param>
    /// <param name="validationError">The output validation error message, if the input is invalid.</param>
    /// <returns>Returns true if the inputs are valid, otherwise false.</returns>
    bool ValidateInputs(HttpRequest req, out string validationError);

    /// <summary>
    /// Constructs the request URL by replacing placeholders with the provided identifier.
    /// </summary>
    /// <param name="baseUrl">The base ARM route containing placeholders (e.g., $subscriptionId).</param>
    /// <param name="identifier">The value to replace the placeholders with.</param>
    /// <returns>Returns the fully constructed URL with placeholders replaced.</returns>
    string BuildRequestUrl(string baseUrl, string identifier);

    /// <summary>
    /// Executes multiple ARM API requests in parallel based on the resource IDs provided.
    /// </summary>
    /// <param name="armRoute">The ARM route template for the requests.</param>
    /// <param name="resourceIds">A list of resource IDs for which the API requests will be executed.</param>
    /// <param name="accessToken">The access token for authenticating the ARM API requests.</param>
    /// <param name="log">The logger instance for logging the execution process.</param>
    /// <returns>Returns a list of JSON responses from the ARM API calls.</returns>
    Task<List<string>> ExecuteFanOutAsync(string armRoute, List<string> resourceIds, string accessToken, ILogger log);

    /// <summary>
    /// Merges multiple JSON responses into a single result.
    /// </summary>
    /// <param name="jsonResponses">The list of JSON responses to be merged.</param>
    /// <returns>Returns the merged JSON as a string.</returns>
    string MergeResults(List<string> jsonResponses);
}
