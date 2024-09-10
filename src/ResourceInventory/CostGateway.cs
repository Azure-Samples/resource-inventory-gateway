namespace ResourceInventory;

/// <summary>
/// The CostGateway class handles requests to the Azure Cost Management API.
/// It provides functionality to fan out API requests for multiple scopes, merge the responses, and format the results.
/// </summary>
public class CostGateway : GatewayFunctionBase
{
    // Static variable to hold all regex patterns for generating generic IDs
    private static readonly Dictionary<string, string> s_patterns = new Dictionary<string, string>
    {
        { @"^/subscriptions/[^/]+/resourceGroups/[^/]+", "/subscriptions/LIST/resourceGroups/LIST" },
        { @"^/subscriptions/[^/]+", "/subscriptions/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+/departments/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST/departments/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+/enrollmentAccounts/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST/enrollmentAccounts/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+/billingProfiles/[^/]+/invoiceSections/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST/billingProfiles/LIST/invoiceSections/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+/billingProfiles/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST/billingProfiles/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+/customers/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST/customers/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST" },
        { @"/providers/Microsoft.Management/managementGroups/[^/]+", "/providers/Microsoft.Management/managementGroups/LIST" }
    };

    /// <summary>
    /// The entry point for the Azure Function. It handles POST requests, validates inputs,
    /// retrieves the access token, calls the Cost Management API for each scope, and merges the results.
    /// </summary>
    /// <param name="req">The HTTP request containing the scope and request body.</param>
    /// <param name="log">The logger instance for logging the execution process.</param>
    /// <returns>Returns an IActionResult containing the merged result from the Cost Management API.</returns>
    [Function("CostGateway")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
        FunctionContext executionContext)
    {
        var log = executionContext.GetLogger("CostGateway");

        log.LogInformation("Processing Cost Management API request.");
        var gateway = new CostGateway();

        // Validate inputs
        if (!gateway.ValidateInputs(req, out var validationError))
        {
            log.LogError($"Validation failed: {validationError}");
            return new BadRequestObjectResult(new { error = "Invalid inputs", details = validationError });
        }

        try
        {
            // Extract the scope and request body from the request
            string scopeParam = req.Query["scope"]!;
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            log.LogInformation($"Received payload: {requestBody}");
            log.LogInformation($"Scopes: {scopeParam}");

            // Retrieve the access token using the method from the base class
            var accessToken = await gateway.GetAccessTokenAsync(log);
            log.LogInformation("Successfully obtained and cached new token.");

            // Split the scope parameter into individual scopes
            var scopes = scopeParam.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                   .Select(scope => "/" + scope.Trim().Trim('\'').Trim('/'))
                                   .ToList();

            // Fan out to call Cost Management API for each scope
            var jsonResponses = await gateway.ExecuteFanOutAsync(requestBody, scopes, accessToken, log);

            // Merge results into a single JSON response using the base class method
            var mergedResult = gateway.MergeResults(jsonResponses);

            // The id is generated based on the scope, we take the first scope for this purpose
            var aScope = scopes.First();

            // Return the response with updated merged JSON
            log.LogInformation($"Cost Management API request processed successfully. {aScope}");
            return new OkObjectResult(UpdateMergedJson(mergedResult, aScope));
        }
        catch (Exception ex)
        {
            log.LogError($"An error occurred while processing the request: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Validates the incoming request to ensure that the 'scope' query parameter is provided.
    /// </summary>
    /// <param name="req">The HTTP request containing the input parameters.</param>
    /// <param name="validationError">Outputs any validation error message.</param>
    /// <returns>Returns true if inputs are valid, false otherwise.</returns>
    public override bool ValidateInputs(HttpRequest req, out string validationError)
    {
        validationError = string.Empty;
        if (string.IsNullOrEmpty(req.Query["scope"]))
        {
            validationError = "The 'scope' query parameter is required.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Builds the request URL for the Cost Management API using the provided scope.
    /// </summary>
    /// <param name="baseUrl">Not used here, as the scope is already part of the URL.</param>
    /// <param name="scope">The scope parameter used to construct the request URL.</param>
    /// <returns>Returns the full URL for the Cost Management API call.</returns>
    public override string BuildRequestUrl(string baseUrl, string scope)
    {
        return $"{baseUrl}{scope}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
    }

    /// <summary>
    /// Executes the Cost Management API requests for each scope in parallel and returns the list of responses.
    /// </summary>
    /// <param name="requestBody">The request payload sent to the Cost Management API.</param>
    /// <param name="scopes">The list of scopes for which the API is called.</param>
    /// <param name="accessToken">The access token for authentication.</param>
    /// <param name="log">The logger instance for logging the execution process.</param>
    /// <returns>Returns a list of JSON responses from the Cost Management API.</returns>
    public override async Task<List<string>> ExecuteFanOutAsync(string requestBody, List<string> scopes, string accessToken, ILogger log)
    {
        var tasks = scopes.Select(scope => CallCostManagementApiAsync(BuildRequestUrl("https://management.azure.com", scope), requestBody, accessToken, log)).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>
    /// Makes a POST request to the Cost Management API and logs the response.
    /// </summary>
    /// <param name="costManagementUrl">The full URL for the Cost Management API call.</param>
    /// <param name="payload">The request payload to send in the POST request.</param>
    /// <param name="accessToken">The access token for authentication.</param>
    /// <param name="log">The logger instance for logging the execution process.</param>
    /// <returns>Returns the JSON response from the Cost Management API as a string.</returns>
    private static async Task<string> CallCostManagementApiAsync(string costManagementUrl, string payload, string accessToken, ILogger log)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            log.LogInformation($"Sending POST request to {costManagementUrl}");
            log.LogInformation($"Payload: {payload}");

            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(costManagementUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                log.LogError($"HTTP request error while calling Cost Management API for URL '{costManagementUrl}': " +
                            $"Status Code: {response.StatusCode} ({(int)response.StatusCode}), " +
                            $"Reason: {response.ReasonPhrase}, " +
                            $"Response: {responseContent}");

                throw new HttpRequestException($"Failed to call Cost Management API. " +
                                            $"Status Code: {response.StatusCode} ({(int)response.StatusCode}), " +
                                            $"Reason: {response.ReasonPhrase}, " +
                                            $"Response: {responseContent}");
            }
            var costResponse = await response.Content.ReadAsStringAsync();
            log.LogInformation($"Received successful response from {costManagementUrl} : {costResponse}");
            return costResponse;
        }
        catch (HttpRequestException ex)
        {
            log.LogError($"HTTP request exception: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            log.LogError($"An unexpected error occurred while calling Cost Management API for URL '{costManagementUrl}': {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Merges multiple responses from the Cost Management API and adds subscription and resource group information
    /// as additional columns.
    /// </summary>
    /// <param name="jsonResponses">The list of JSON responses to merge.</param>
    /// <returns>Returns the merged JSON result as a string.</returns>
    public override string MergeResults(List<string> jsonResponses)
    {
        var mergedRows = new List<JsonElement>();
        List<JsonElement>? mergedColumns = null;

        foreach (var jsonResponse in jsonResponses)
        {
            var jsonDocument = JsonDocument.Parse(jsonResponse);
            var rootElement = jsonDocument.RootElement;

            // Extract the 'id' from the response
            var id = rootElement.GetProperty("id").GetString()!;

            // Use the tuple method to extract subscriptionId and resourceGroupName
            var (subscriptionId, resourceGroupName) = ExtractSubscriptionAndResourceGroup(id);

            // Extract the columns and rows
            var properties = rootElement.GetProperty("properties");
            var columns = properties.GetProperty("columns").EnumerateArray().ToList();
            var rows = properties.GetProperty("rows").EnumerateArray().ToList();

            // Add subscription and resourceGroup columns if not already added
            if (mergedColumns == null)
            {
                mergedColumns = new List<JsonElement>(columns)
                {
                    JsonDocument.Parse("{\"name\": \"_subscription\", \"type\": \"String\"}").RootElement
                };
                if (resourceGroupName != null)
                {
                    mergedColumns.Add(JsonDocument.Parse("{\"name\": \"_resourceGroup\", \"type\": \"String\"}").RootElement);
                }
            }

            // Add subscription and resourceGroup values to each row
            foreach (var row in rows)
            {
                var rowList = row.EnumerateArray().ToList();
                rowList.Add(JsonDocument.Parse($"\"{subscriptionId}\"").RootElement);

                if (resourceGroupName != null)
                {
                    rowList.Add(JsonDocument.Parse($"\"{resourceGroupName}\"").RootElement);
                }

                mergedRows.Add(JsonDocument.Parse(JsonSerializer.Serialize(rowList)).RootElement);
            }
        }

        // Create the final JSON structure
        var finalJsonDocument = new
        {
            id = (string?)null,  // Placeholder, to be set by UpdateMergedJson
            name = (string?)null,  // Placeholder, to be set by UpdateMergedJson
            type = "Microsoft.CostManagement/query",
            properties = new
            {
                columns = mergedColumns,
                rows = mergedRows
            }
        };

        return JsonSerializer.Serialize(finalJsonDocument, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Extracts the subscription ID and resource group name from the provided resource ID.
    /// </summary>
    /// <param name="id">The resource ID string.</param>
    /// <returns>Returns a tuple containing the subscription ID and resource group name.</returns>
    private static (string? subscriptionId, string? resourceGroupName) ExtractSubscriptionAndResourceGroup(string id)
    {
        string? subscriptionId = null;
        string? resourceGroupName = null;

        var subscriptionMatch = Regex.Match(id, @"subscriptions\/([^\/]+)", RegexOptions.IgnoreCase);
        if (subscriptionMatch.Success)
        {
            subscriptionId = subscriptionMatch.Groups[1].Value;
        }

        var resourceGroupMatch = Regex.Match(id, @"resourceGroups\/([^\/]+)", RegexOptions.IgnoreCase);
        if (resourceGroupMatch.Success)
        {
            resourceGroupName = resourceGroupMatch.Groups[1].Value;
        }

        return (subscriptionId, resourceGroupName);
    }

    /// <summary>
    /// Generates a generic ID based on the scope using predefined regex patterns.
    /// </summary>
    /// <param name="scope">The scope from which to generate the generic ID.</param>
    /// <returns>Returns the generated generic ID.</returns>
    private static string GenerateGenericId(string scope)
    {
        foreach (var pattern in s_patterns)
        {
            if (Regex.IsMatch(scope, pattern.Key))
            {
                return Regex.Replace(scope, pattern.Key, pattern.Value);
            }
        }
        return scope; // Return the original scope if no pattern matched
    }

    /// <summary>
    /// Updates the merged JSON with a generated ID and name based on the provided scope.
    /// </summary>
    /// <param name="mergedJson">The merged JSON string to update.</param>
    /// <param name="scope">The scope used to generate the new ID and name.</param>
    /// <returns>Returns the updated JSON with the new ID and name.</returns>
    private static string UpdateMergedJson(string mergedJson, string scope)
    {
        // Generate the generic ID based on the scope
        var genericId = GenerateGenericId(scope);
        var generatedGuid = Guid.NewGuid().ToString();

        // Parse the JSON to a JsonDocument to modify it
        using var document = JsonDocument.Parse(mergedJson);
        var root = document.RootElement;

        using var outputStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(outputStream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); // Start root object

            // Directly write the updated "id" and "name" fields
            writer.WriteString("id", $"{genericId}/{generatedGuid}");
            writer.WriteString("name", generatedGuid);

            // Write the other properties from the original JSON
            writer.WritePropertyName("type");
            root.GetProperty("type").WriteTo(writer);

            writer.WritePropertyName("properties");
            root.GetProperty("properties").WriteTo(writer);

            writer.WriteEndObject(); // End root object
        }

        return Encoding.UTF8.GetString(outputStream.ToArray());
    }
}
