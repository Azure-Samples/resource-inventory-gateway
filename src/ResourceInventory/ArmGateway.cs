namespace ResourceInventory;

/// <summary>
/// The ArmGateway class handles incoming requests to interact with ARM APIs. It extends the GatewayFunctionBase class
/// to utilize common functionality like authentication.
/// </summary>
public class ArmGateway : GatewayFunctionBase
{
    /// <summary>
    /// The entry point for the Azure Function. It handles GET requests, validates inputs, retrieves an access token,
    /// and fans out the ARM API calls for each provided resource ID.
    /// </summary>
    /// <param name="req">The HTTP request, containing query parameters like armRoute and resourceIds.</param>
    /// <param name="log">The logger instance for logging the execution process.</param>
    /// <returns>Returns an IActionResult containing the result of the API calls.</returns>
    [Function("ArmGateway")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
        FunctionContext executionContext)
    {
        var log = executionContext.GetLogger("ArmGateway");

        log.LogInformation("Processing ARM API request.");
        var gateway = new ArmGateway();

        // Validate inputs
        if (!gateway.ValidateInputs(req, out var validationError))
        {
            log.LogError($"Validation failed: {validationError}");
            return new BadRequestObjectResult(new { error = "Invalid inputs", details = validationError });
        }

        try
        {
            // Extract the ARM route and resource IDs from the request
            string armRoute = req.Query["armRoute"]!;
            string resourceIdsParam = req.Query["resourceIds"]!;
            var resourceIds = resourceIdsParam.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                              .Select(id => id.Trim('\'')).ToList();

            log.LogInformation($"ARM Route: {armRoute}");
            log.LogInformation($"Resource IDs: {string.Join(", ", resourceIds)}");

            // Retrieve the access token using the method from the base class
            var accessToken = await gateway.GetAccessTokenAsync(log);
            log.LogInformation("Successfully obtained and cached new token.");

            // Fan out to call ARM API for each resource ID
            var jsonResponses = await gateway.ExecuteFanOutAsync(armRoute, resourceIds, accessToken, log);

            // Return the response
            log.LogInformation("ARM API request processed successfully.");
            return new OkObjectResult(jsonResponses.First());
        }
        catch (Exception ex)
        {
            log.LogError($"An error occurred while processing the request: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Validates the incoming request to ensure the armRoute and resourceIds are provided.
    /// </summary>
    /// <param name="req">The HTTP request containing input parameters.</param>
    /// <param name="validationError">Outputs any validation error message.</param>
    /// <returns>Returns true if inputs are valid, false otherwise.</returns>
    public override bool ValidateInputs(HttpRequest req, out string validationError)
    {
        validationError = string.Empty;
        if (string.IsNullOrEmpty(req.Query["armRoute"]) || string.IsNullOrEmpty(req.Query["resourceIds"]))
        {
            validationError = "Both 'armRoute' and 'resourceIds' query parameters are required.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Builds the request URL by replacing the placeholders in the ARM route with actual values from the resource ID.
    /// </summary>
    /// <param name="armRoute">The base ARM route containing placeholders.</param>
    /// <param name="resourceId">The resource ID used to replace the placeholders.</param>
    /// <returns>Returns the constructed URL with placeholders replaced.</returns>
    public override string BuildRequestUrl(string armRoute, string resourceId)
    {
        var paramNames = ExtractParameterNames(armRoute);
        return ReplaceMarkersWithValues(armRoute, paramNames, resourceId);
    }

    /// <summary>
    /// Executes ARM API requests in parallel for each resource ID, passing the access token for authentication.
    /// It also merges the results after all requests are completed.
    /// </summary>
    /// <param name="armRoute">The ARM route to execute.</param>
    /// <param name="resourceIds">The list of resource IDs for which to execute the ARM API calls.</param>
    /// <param name="accessToken">The access token used to authenticate the ARM API requests.</param>
    /// <param name="log">The logger instance for logging the execution process.</param>
    /// <returns>Returns a list of merged JSON responses.</returns>
    public override async Task<List<string>> ExecuteFanOutAsync(string armRoute, List<string> resourceIds, string accessToken, ILogger log)
    {
        var tasks = new List<Task<string>>();
        var parameterValuesList = new List<Dictionary<string, string>>();

        foreach (var resourceId in resourceIds)
        {
            var paramNames = ExtractParameterNames(armRoute);
            var paramValues = ExtractParameterValues(paramNames, resourceId);
            parameterValuesList.Add(paramValues);

            var routeWithValues = ReplaceMarkersWithValues(armRoute, paramNames, resourceId);
            tasks.Add(CallArmApiAsync(routeWithValues, accessToken, log));
        }

        var responses = await Task.WhenAll(tasks);

        // Pass the parameterValuesList to MergeResults
        return new List<string> { MergeResults(responses.ToList(), parameterValuesList) };
    }

    /// <summary>
    /// Calls the ARM API for the given route and logs the response.
    /// </summary>
    /// <param name="routeWithValues">The fully constructed ARM route with parameter values.</param>
    /// <param name="accessToken">The access token used to authenticate the ARM API request.</param>
    /// <param name="log">The logger instance for logging the execution process.</param>
    /// <returns>Returns the JSON response from the ARM API call as a string.</returns>
    private static async Task<string> CallArmApiAsync(string routeWithValues, string accessToken, ILogger log)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Log the full URL before making the request
            var fullUrl = $"https://management.azure.com{routeWithValues}";
            log.LogInformation($"Calling ARM API: {fullUrl}");

            var response = await client.GetAsync(fullUrl);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            log.LogInformation($"ARM API response: {content}");

            return content;
        }
        catch (HttpRequestException ex)
        {
            log.LogError($"HTTP request error while calling ARM API: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            log.LogError($"An unexpected error occurred while calling ARM API: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Extracts parameter names (placeholders prefixed with '$') from the ARM route.
    /// </summary>
    /// <param name="armRoute">The ARM route containing placeholders like $subscriptionId or $resourceGroupName.</param>
    /// <returns>Returns a list of extracted parameter names.</returns>
    private static List<string> ExtractParameterNames(string armRoute)
    {
        var parameterNames = new List<string>();

        // Split the route into path and query parts
        var routeParts = armRoute.Split('?');

        // Handle the path part
        var pathParts = routeParts[0].Split('/');
        foreach (var part in pathParts)
        {
            if (part.StartsWith("$"))
            {
                // Add the parameter name without the $ prefix
                parameterNames.Add(part.Substring(1));
            }
        }
        return parameterNames;
    }

    /// <summary>
    /// Extracts parameter values from the resource ID based on the parameter names extracted from the ARM route.
    /// </summary>
    /// <param name="parameterNames">A list of parameter names extracted from the ARM route.</param>
    /// <param name="resourceId">The resource ID to extract values from.</param>
    /// <returns>Returns a dictionary of parameter names and their corresponding values from the resource ID.</returns>
    private static Dictionary<string, string> ExtractParameterValues(List<string> parameterNames, string resourceId)
    {
        var parameterValues = new Dictionary<string, string>();

        foreach (var paramName in parameterNames)
        {
            var regex = new Regex($@"{paramName}\/([^\/]+)", RegexOptions.IgnoreCase);
            var match = regex.Match(resourceId);
            if (match.Success)
            {
                parameterValues[paramName] = match.Groups[1].Value;
            }
            else
            {
                parameterValues[paramName] = "null"; // Handle as appropriate
            }
        }

        return parameterValues;
    }

    /// <summary>
    /// Merges the ARM API responses and includes extracted parameter values in the 'gateway' element.
    /// </summary>
    /// <param name="jsonResponses">The list of JSON responses from the ARM API calls.</param>
    /// <param name="parameterValuesList">The list of dictionaries containing parameter values extracted from resource IDs.</param>
    /// <returns>Returns the merged JSON as a string.</returns>
    public string MergeResults(List<string> jsonResponses, List<Dictionary<string, string>> parameterValuesList)
    {
        var mergedItems = new List<JsonElement>();

        for (var i = 0; i < jsonResponses.Count; i++)
        {
            var responseJson = jsonResponses[i];
            var parameterValues = parameterValuesList[i];

            using (var doc = JsonDocument.Parse(responseJson))
            {
                // Handle case where 'value' is an array of items
                if (doc.RootElement.TryGetProperty("value", out var valueArray))
                {
                    foreach (var item in valueArray.EnumerateArray())
                    {
                        var updatedItem = AddGatewayElementToItem(item, parameterValues);
                        mergedItems.Add(updatedItem);
                    }
                }
                else
                {
                    // Handle case where the root itself is a single object, not an array
                    var singleItem = doc.RootElement.Clone();
                    var updatedItem = AddGatewayElementToItem(singleItem, parameterValues);
                    mergedItems.Add(updatedItem);
                }
            }
        }

        // Reconstruct the final merged JSON with all items under 'value'
        var finalJson = new
        {
            value = mergedItems
        };

        return JsonSerializer.Serialize(finalJson, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Adds a 'gateway' element to the JSON object, containing the extracted parameter values.
    /// </summary>
    /// <param name="item">The JSON object to update.</param>
    /// <param name="parameterValues">The extracted parameter values to include in the 'gateway' element.</param>
    /// <returns>Returns the updated JSON object with the 'gateway' element.</returns>
    private static JsonElement AddGatewayElementToItem(JsonElement item, Dictionary<string, string> parameterValues)
    {
        // Initialize a dictionary to store the updated item
        var updatedItem = new Dictionary<string, JsonElement>();

        // Copy all existing fields to the updated item
        foreach (var prop in item.EnumerateObject())
        {
            updatedItem[prop.Name] = prop.Value.Clone();
        }

        // Create a new 'gateway' element to store the parameters
        var gatewayDict = new Dictionary<string, JsonElement>();
        foreach (var param in parameterValues)
        {
            gatewayDict[$"{param.Key}"] = JsonDocument.Parse($"\"{param.Value}\"").RootElement;
        }

        // Serialize 'gateway' into a JSON element
        var gatewayJson = JsonSerializer.Serialize(gatewayDict);
        updatedItem["gateway"] = JsonDocument.Parse(gatewayJson).RootElement;

        // Convert updatedItem back to JsonElement
        var updatedItemJson = JsonSerializer.Serialize(updatedItem);
        return JsonDocument.Parse(updatedItemJson).RootElement;
    }

    /// <summary>
    /// Replaces placeholders in the ARM route with actual values extracted from the resource ID.
    /// </summary>
    /// <param name="armRoute">The ARM route containing placeholders like $subscriptionId or $resourceGroupName.</param>
    /// <param name="parameterNames">The list of parameter names to replace.</param>
    /// <param name="resourceId">The resource ID containing the actual values.</param>
    /// <returns>Returns the ARM route with placeholders replaced by actual values.</returns>
    private static string ReplaceMarkersWithValues(string armRoute, List<string> parameterNames, string resourceId)
    {
        var resourceParts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var paramName in parameterNames)
        {
            var index = Array.IndexOf(resourceParts, paramName);

            if (index >= 0 && index < resourceParts.Length - 1)
            {
                var valueToReplace = resourceParts[index + 1];
                var marker = $"${paramName}";
                armRoute = armRoute.Replace(marker, valueToReplace);
            }
            else
            {
                throw new KeyNotFoundException($"The parameter '{paramName}' does not have a corresponding value in the resourceId '{resourceId}'.");
            }
        }

        return armRoute;
    }
}
