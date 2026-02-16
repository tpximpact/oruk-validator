using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Json.Schema;
using OpenReferralApi.Core.Models;
using ValidationError = OpenReferralApi.Core.Models.ValidationError;

namespace OpenReferralApi.Core.Services;

public interface IJsonValidatorService
{
    Task<ValidationResult> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateWithSchemaUriAsync(object jsonData, string schemaUri, ValidationOptions? options = null, CancellationToken cancellationToken = default);
    Task<bool> IsValidAsync(ValidationRequest request, CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateSchemaAsync(object schema, CancellationToken cancellationToken = default);
}

public class JsonValidatorService : IJsonValidatorService
{
    private readonly ILogger<JsonValidatorService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IPathParsingService _pathParsingService;
    private readonly IRequestProcessingService _requestProcessingService;
    private readonly ISchemaResolverService _schemaResolverService;
    private readonly IJsonSerializationOptionsProvider _jsonSerializationOptionsProvider;

    public JsonValidatorService(
        ILogger<JsonValidatorService> logger,
        HttpClient httpClient,
        IPathParsingService pathParsingService,
        IRequestProcessingService requestProcessingService,
        ISchemaResolverService schemaResolverService,
        IJsonSerializationOptionsProvider jsonSerializationOptionsProvider)
    {
        _logger = logger;
        _httpClient = httpClient;
        _pathParsingService = pathParsingService;
        _requestProcessingService = requestProcessingService;
        _schemaResolverService = schemaResolverService;
        _jsonSerializationOptionsProvider = jsonSerializationOptionsProvider;
    }

    public async Task<ValidationResult> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken = default)
    {
        return await _requestProcessingService.ExecuteWithConcurrencyControlAsync(
            ct => ValidateCoreAsync(request, ct),
            request.Options,
            cancellationToken);
    }

    public async Task<ValidationResult> ValidateWithSchemaUriAsync(object jsonData, string schemaUri, ValidationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = new ValidationRequest
        {
            JsonData = jsonData,
            SchemaUri = schemaUri,
            Options = options
        };

        return await _requestProcessingService.ExecuteWithConcurrencyControlAsync(
            ct => ValidateCoreAsync(request, ct),
            options,
            cancellationToken);
    }

    public async Task<bool> IsValidAsync(ValidationRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _requestProcessingService.ExecuteWithConcurrencyControlAsync(
            ct => ValidateCoreAsync(request, ct),
            request.Options,
            cancellationToken);
        return result.IsValid;
    }

    private async Task<ValidationResult> ValidateCoreAsync(ValidationRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ValidationResult();

        try
        {
            _logger.LogInformation("Starting JSON validation for request");

            // Create timeout token
            using var timeoutCts = _requestProcessingService.CreateTimeoutToken(request.Options, cancellationToken);
            var effectiveToken = timeoutCts.Token;

            // Get JSON data and schema concurrently if possible
            var dataTask = GetJsonDataAsync(request, effectiveToken);
            var schemaTask = GetSchemaAsync(request, effectiveToken);

            var jsonData = await dataTask;
            var schema = await schemaTask;

            // Validate the JSON data
            // Format with indentation so validation error line numbers are accurate
            var jsonDataString = JsonSerializer.Serialize(jsonData, _jsonSerializationOptionsProvider.PrettyPrintOptions);
            var validationErrors = await ValidateJsonAgainstSchemaAsync(jsonDataString, schema, request.Options);

            // Build result
            result.IsValid = !validationErrors.Any();
            result.Errors = validationErrors;
            result.SchemaVersion = "2020-12";
            result.Metadata = new ValidationMetadata
            {
                SchemaTitle = GetSchemaTitle(request, schema),
                SchemaDescription = GetSchemaDescription(request, schema),
                DataSize = jsonDataString.Length,
                ValidationTimestamp = DateTime.UtcNow,
                DataSource = !string.IsNullOrEmpty(request.DataUrl) ? request.DataUrl : "direct"
            };

            _logger.LogInformation("JSON validation completed. IsValid: {IsValid}, Errors: {ErrorCount}",
                result.IsValid, result.Errors.Count);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Invalid argument during JSON validation");
            throw;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid operation during JSON validation");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during JSON validation");
            result.IsValid = false;
            result.Errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Validation failed: {ex.Message}",
                ErrorCode = "VALIDATION_ERROR",
                Severity = "Error"
            });
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
        }

        return result;
    }

    public async Task<ValidationResult> ValidateSchemaAsync(object schema, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ValidationResult();

        try
        {
            _logger.LogInformation("Starting schema validation");

            var schemaJson = JsonSerializer.Serialize(schema);
            var jsonSchema = await _schemaResolverService.CreateSchemaFromJsonAsync(schemaJson, cancellationToken);

            // Basic schema validation
            var schemaValidationErrors = new List<ValidationError>();

            // Check if schema has a type property
            var schemaNode = JsonNode.Parse(schemaJson);
            if (schemaNode is JsonObject schemaObject && !schemaObject.ContainsKey("type"))
            {
                schemaValidationErrors.Add(new ValidationError
                {
                    Path = "",
                    Message = "Schema is missing required 'type' property",
                    ErrorCode = "MISSING_TYPE",
                    Severity = "Error"
                });
            }

            result.IsValid = !schemaValidationErrors.Any();
            result.Errors = schemaValidationErrors;
            result.SchemaVersion = "2020-12";
            result.Metadata = new ValidationMetadata
            {
                SchemaTitle = GetSchemaTitleFromObject(schema),
                SchemaDescription = GetSchemaDescriptionFromObject(schema),
                ValidationTimestamp = DateTime.UtcNow
            };

            _logger.LogInformation("Schema validation completed. IsValid: {IsValid}", result.IsValid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during schema validation");
            result.IsValid = false;
            result.Errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Schema validation failed: {ex.Message}",
                ErrorCode = "SCHEMA_VALIDATION_ERROR",
                Severity = "Error"
            });
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
        }

        return result;
    }

    private async Task<object> GetJsonDataAsync(ValidationRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.DataUrl))
        {
            return await FetchJsonDataFromUrlAsync(request.DataUrl, request.Options, cancellationToken);
        }
        else if (request.JsonData != null)
        {
            return request.JsonData;
        }
        else
        {
            throw new ArgumentException("Either JsonData or DataUrl must be provided");
        }
    }

    private async Task<JsonSchema> GetSchemaAsync(ValidationRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.SchemaUri))
        {
            return await LoadSchemaFromUriAsync(request.SchemaUri, request.Options, cancellationToken);
        }
        else if (request.Schema != null)
        {
            return await CreateSchemaFromObjectAsync(request.Schema);
        }
        else
        {
            throw new ArgumentException("Either Schema, SchemaUri, or SchemaId must be provided");
        }
    }

    private async Task<JsonSchema> LoadSchemaFromUriAsync(string schemaUri, ValidationOptions? options, CancellationToken cancellationToken)
    {
        return await _requestProcessingService.ExecuteWithRetryAsync(async (ct) =>
        {
            try
            {
                _logger.LogInformation("Loading schema from URI: {SchemaUri}", schemaUri);

                // Use PathParsingService for URI validation
                var validatedUri = await _pathParsingService.ValidateAndParseSchemaUriAsync(schemaUri, options);

                var response = await _httpClient.GetAsync(validatedUri, ct);
                response.EnsureSuccessStatusCode();
                var schemaJson = await response.Content.ReadAsStringAsync(ct);

                // Resolve all $ref references before creating the schema to prevent RefResolutionException during evaluation
                var resolvedSchemaJson = await _schemaResolverService.ResolveAsync(schemaJson, validatedUri.ToString());

                // Create schema from the fully resolved JSON
                return await _schemaResolverService.CreateSchemaFromJsonAsync(resolvedSchemaJson, validatedUri.ToString(), null, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load schema from URI: {SchemaUri}", schemaUri);
                throw new InvalidOperationException($"Failed to load schema from URI: {schemaUri}", ex);
            }
        }, options, cancellationToken);
    }

    private async Task<JsonSchema> CreateSchemaFromObjectAsync(object schema)
    {
        try
        {
            var schemaJson = JsonSerializer.Serialize(schema);
            // Resolve all $ref references before creating the schema to prevent RefResolutionException during evaluation
            var resolvedSchemaJson = await _schemaResolverService.ResolveAsync(schemaJson);
            return await _schemaResolverService.CreateSchemaFromJsonAsync(resolvedSchemaJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create schema from object");
            throw new InvalidOperationException("Failed to create schema from object", ex);
        }
    }

    private Task<List<ValidationError>> ValidateJsonAgainstSchemaAsync(string jsonData, JsonSchema schema, ValidationOptions? options)
    {
        var errors = new List<ValidationError>();
        var maxErrors = options?.MaxErrors ?? 100;

        try
        {
            // Parse JSON directly as JsonDocument for validation (avoid double serialization)
            using var doc = JsonDocument.Parse(jsonData);
            var validationResults = schema.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions
            {
                OutputFormat = Json.Schema.OutputFormat.List
            });

            if (!validationResults.IsValid)
            {
                // Convert JsonSchema.Net validation results to our ValidationError format
                var failedDetails = validationResults.Details?.Where(d => !d.IsValid).Take(maxErrors) ?? Enumerable.Empty<EvaluationResults>();
                foreach (var error in failedDetails)
                {
                    var errorPath = error.InstanceLocation.ToString();
                    var errorMessage = BuildValidationMessage(error, errorPath);
                    var errorCode = ExtractErrorCode(error);
                    
                    errors.Add(new ValidationError
                    {
                        Path = errorPath,
                        Message = errorMessage,
                        ErrorCode = errorCode,
                        Severity = "Error"
                    });
                }
            }
        }
        catch (JsonException ex)
        {
            errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Invalid JSON format: {ex.Message}",
                ErrorCode = "INVALID_JSON",
                Severity = "Error"
            });
        }
        catch (Exception ex)
        {
            errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Validation error: {ex.Message}",
                ErrorCode = "VALIDATION_ERROR",
                Severity = "Error"
            });
        }

        return Task.FromResult(errors);
    }

    private static string BuildValidationMessage(EvaluationResults results, string errorPath)
    {
        var errorDetails = FormatValidationErrors(results);
        if (!string.IsNullOrWhiteSpace(errorDetails))
        {
            return errorDetails;
        }

        // If no specific error details, try to build a message from schema location
        var schemaKeyword = GetSchemaKeywordFromResults(results);
        if (!string.IsNullOrWhiteSpace(schemaKeyword))
        {
            return string.IsNullOrWhiteSpace(errorPath)
                ? $"Failed {schemaKeyword} validation"
                : $"Failed {schemaKeyword} validation at {errorPath}";
        }

        return string.IsNullOrWhiteSpace(errorPath)
            ? "Validation failed."
            : "Validation failed at " + errorPath;
    }

    private static string? FormatValidationErrors(EvaluationResults results)
    {
        if (results.Errors == null || results.Errors.Count == 0)
        {
            return null;
        }

        var errorMessages = results.Errors
            .Select(kvp => 
            {
                if (string.IsNullOrWhiteSpace(kvp.Value))
                {
                    return kvp.Key;
                }
                
                // Clean up error message format
                var message = kvp.Value;
                if (message.StartsWith("\"") && message.EndsWith("\""))
                {
                    message = message.Trim('"');
                }
                
                return $"{kvp.Key}: {message}";
            })
            .ToArray();

        return errorMessages.Length == 0 ? null : string.Join("; ", errorMessages);
    }

    private static string? GetSchemaKeywordFromResults(EvaluationResults results)
    {
        // Try to extract the schema keyword from the schema location
        if (results.SchemaLocation != null)
        {
            var schemaPath = results.SchemaLocation.ToString();
            var parts = schemaPath.Split('/');
            if (parts.Length > 0)
            {
                var keyword = parts.LastOrDefault();
                if (!string.IsNullOrWhiteSpace(keyword) && keyword != "#")
                {
                    return keyword.ToLowerInvariant();
                }
            }
        }

        // Fallback: look for keywords in error dictionary
        if (results.Errors?.Count > 0)
        {
            var firstKeyword = results.Errors.Keys.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstKeyword))
            {
                return firstKeyword.ToLowerInvariant();
            }
        }

        return null;
    }

    private static string ExtractErrorCode(EvaluationResults results)
    {
        // Map schema keywords to error codes
        if (results.Errors?.Count > 0)
        {
            var keyword = results.Errors.Keys.First();
            return keyword switch
            {
                "required" => "REQUIRED_FIELD_MISSING",
                "type" => "INVALID_TYPE",
                "enum" => "INVALID_VALUE",
                "minimum" => "VALUE_TOO_SMALL",
                "maximum" => "VALUE_TOO_LARGE",
                "pattern" => "INVALID_FORMAT",
                "minLength" => "STRING_TOO_SHORT",
                "maxLength" => "STRING_TOO_LONG",
                "minItems" => "ARRAY_TOO_SHORT",
                "maxItems" => "ARRAY_TOO_LONG",
                "additionalProperties" => "UNEXPECTED_PROPERTY",
                "oneOf" => "SCHEMA_MISMATCH",
                "anyOf" => "NO_SCHEMA_MATCH",
                "allOf" => "SCHEMA_MISMATCH",
                "not" => "SCHEMA_MISMATCH",
                "ref" => "SCHEMA_REFERENCE_ERROR",
                _ => "VALIDATION_ERROR"
            };
        }

        return "VALIDATION_ERROR";
    }

    private async Task<object> FetchJsonDataFromUrlAsync(string dataUrl, ValidationOptions? options, CancellationToken cancellationToken)
    {
        return await _requestProcessingService.ExecuteWithRetryAsync(async (ct) =>
        {
            try
            {
                _logger.LogInformation("Fetching JSON data from URL: {DataUrl}", dataUrl);

                // Use PathParsingService for URL validation
                var validatedUri = await _pathParsingService.ValidateAndParseDataUrlAsync(dataUrl, options);

                using var request = new HttpRequestMessage(HttpMethod.Get, validatedUri);

                var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(ct);

                // Parse and return the JSON data
                return JsonSerializer.Deserialize<object>(content)
                    ?? throw new InvalidOperationException("Failed to deserialize JSON data from URL");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request failed when fetching data from URL: {DataUrl}", dataUrl);
                throw new InvalidOperationException($"Failed to fetch data from URL: {dataUrl}", ex);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Invalid JSON received from URL: {DataUrl}", dataUrl);
                throw new InvalidOperationException($"Invalid JSON received from URL: {dataUrl}", ex);
            }
        }, options, cancellationToken);
    }

    private string? GetSchemaTitle(ValidationRequest request, JsonSchema schema)
    {
        // First try to get the title from the original schema object
        if (request.Schema != null)
        {
            try
            {
                var jsonElement = JsonSerializer.SerializeToElement(request.Schema);
                if (jsonElement.TryGetProperty("title", out var titleElement))
                {
                    var title = titleElement.GetString();
                    if (!string.IsNullOrEmpty(title))
                    {
                        return title;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to extract title from original schema object");
            }
        }

        // JsonSchema.Net doesn't provide direct Title property, so we return null if not in original schema
        return null;
    }

    private string? GetSchemaDescription(ValidationRequest request, JsonSchema schema)
    {
        // First try to get the description from the original schema object
        if (request.Schema != null)
        {
            try
            {
                var jsonElement = JsonSerializer.SerializeToElement(request.Schema);
                if (jsonElement.TryGetProperty("description", out var descElement))
                {
                    var description = descElement.GetString();
                    if (!string.IsNullOrEmpty(description))
                    {
                        return description;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to extract description from original schema object");
            }
        }

        // JsonSchema.Net doesn't provide direct Description property, so we return null if not in original schema
        return null;
    }

    private string? GetSchemaTitleFromObject(object? schemaObject)
    {
        if (schemaObject == null) return null;
        
        try
        {
            var jsonElement = JsonSerializer.SerializeToElement(schemaObject);
            if (jsonElement.TryGetProperty("title", out var titleElement))
            {
                return titleElement.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract title from schema object");
        }
        return null;
    }

    private string? GetSchemaDescriptionFromObject(object? schemaObject)
    {
        if (schemaObject == null) return null;
        
        try
        {
            var jsonElement = JsonSerializer.SerializeToElement(schemaObject);
            if (jsonElement.TryGetProperty("description", out var descElement))
            {
                return descElement.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract description from schema object");
        }
        return null;
    }

}

