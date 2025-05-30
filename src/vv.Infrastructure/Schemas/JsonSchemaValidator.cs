﻿using System.Text.Json;
using Json.Schema;
using vv.Core.Validation;
using vv.Domain.Validation; // Your ValidationResult location

namespace vv.Infrastructure.Schemas;

public class JsonSchemaValidator
{
    private readonly JsonSchema _schema;

    public JsonSchemaValidator(string schemaFilePath)
    {
        ArgumentNullException.ThrowIfNull(schemaFilePath);
        try
        {
            var schemaJson = File.ReadAllText(schemaFilePath);
            _schema = JsonSchema.FromText(schemaJson);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to read schema file: {schemaFilePath}", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse schema JSON from file: {schemaFilePath}", ex);
        }
    }

    public ValidationResult Validate(string jsonPayload)
    {
        try
        {
            JsonElement element;
            try
            {
                element = JsonDocument.Parse(jsonPayload).RootElement;
            }
            catch (JsonException ex)
            {
                // Handle JSON parsing errors gracefully
                return ValidationResult.Failure(new List<ValidationError>
                {
                    new ValidationError
                    {
                        ErrorMessage = $"Invalid JSON format: {ex.Message}",
                        Source = "JsonSchema.Net"
                    }
                });
            }

            // Run validation directly
            var results = _schema.Validate(element);

            if (results.IsValid)
                return ValidationResult.Success();

            var errors = GetAllMessages(results)
                .Select(msg => new ValidationError
                {
                    ErrorMessage = msg,
                    Source = "JsonSchema.Net"
                })
                .ToList();

            return ValidationResult.Failure(errors);
        }
        catch (Exception ex)
        {
            // Handle any other unexpected errors
            return ValidationResult.Failure(new List<ValidationError>
            {
                new ValidationError
                {
                    ErrorMessage = $"Validation error: {ex.Message}",
                    Source = "JsonSchema.Net"
                }
            });
        }
    }

    private static IEnumerable<string> GetAllMessages(ValidationResults results)
    {
        if (!string.IsNullOrWhiteSpace(results.Message))
            yield return results.Message;

        if (results.NestedResults != null)
        {
            foreach (var nested in results.NestedResults)
            {
                foreach (var msg in GetAllMessages(nested))
                    yield return msg;
            }
        }
    }
}