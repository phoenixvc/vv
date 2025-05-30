using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Microsoft.Extensions.Logging;
using vv.Domain.Configuration;
using vv.Domain.Events;
using vv.Domain.Models;

namespace vv.Infrastructure.Events
{
    public class EventGridPublisher : IEventPublisher
    {
        private readonly EventGridPublisherClient _client;
        private readonly ILogger<EventGridPublisher> _logger;
        private readonly string _baseSourceUri;

        // Event Grid limits
        private const int MaxEventsPerBatch = 100;
        private const int MaxPayloadSizeBytes = 1024 * 1024; // 1MB

        // Constructor for direct DI usage
        public EventGridPublisher(EventGridPublisherClient client, ILogger<EventGridPublisher> logger, string baseSourceUri = "https://vv/events/")
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (!Uri.TryCreate(baseSourceUri, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
            {
                throw new ArgumentException("baseSourceUri must be a valid absolute URI", nameof(baseSourceUri));
            }
            _baseSourceUri = baseSourceUri;
        }

        // Async factory for secret-based construction
        public static async Task<EventGridPublisher> CreateAsync(
            IMarketDataSecretProvider secretProvider,
            ILogger<EventGridPublisher> logger)
        {
            if (secretProvider == null) throw new ArgumentNullException(nameof(secretProvider));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            try
            {
                var endpoint = await secretProvider.GetEventGridEndpointAsync();
                var key = await secretProvider.GetEventGridKeyAsync();

                // Validate secrets
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new ArgumentException("Event Grid endpoint cannot be null or empty", nameof(endpoint));
                }

                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("Event Grid key cannot be null or empty", nameof(key));
                }

                // Validate endpoint is a valid URI
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                {
                    throw new ArgumentException($"Invalid Event Grid endpoint URI: {endpoint}", nameof(endpoint));
                }

                var client = new EventGridPublisherClient(uri, new AzureKeyCredential(key));
                return new EventGridPublisher(client, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create EventGridPublisher");
                throw;
            }
        }

        // Generic event publish (CloudEvent, batch-safe) - Updated with CancellationToken
        public async Task PublishAsync<T>(T eventData, string? topic = null, CancellationToken cancellationToken = default) where T : class
        {
            if (eventData == null) throw new ArgumentNullException(nameof(eventData));

            try
            {
                var eventType = typeof(T).Name;
                var topicName = topic ?? DeriveTopicFromEventType(eventType);
                var source = BuildSourceUri(topicName); // Ensures a valid URI

                var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                var data = BinaryData.FromObjectAsJson(eventData, options);
                var cloudEvent = new CloudEvent(
                    source.ToString(),
                    eventType,
                    data,
                    "application/json",
                    CloudEventDataFormat.Json
                )
                {
                    Id = Guid.NewGuid().ToString(),
                    Time = DateTimeOffset.UtcNow
                };

                // Add logical topic name as a custom attribute for easier filtering/routing
                cloudEvent.ExtensionAttributes.Add("topic", topicName);

                await _client.SendEventAsync(cloudEvent, cancellationToken);
                _logger.LogInformation("Published event {EventType} with source {Source}", eventType, source);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish event {EventType}", typeof(T).Name);
                throw;
            }
        }

        // Batch publish with chunking and size limits - Updated with CancellationToken
        public async Task PublishManyAsync<T>(IEnumerable<T> events, string? topic = null, CancellationToken cancellationToken = default) where T : class
        {
            var eventsList = events?.ToList() ?? new List<T>();
            if (!eventsList.Any())
                return;

            try
            {
                var eventType = typeof(T).Name;
                var topicName = topic ?? DeriveTopicFromEventType(eventType);
                var source = BuildSourceUri(topicName);

                var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                // Convert all events to CloudEvents first, checking payload size
                var allCloudEvents = new List<CloudEvent>();
                foreach (var e in eventsList)
                {
                    var data = BinaryData.FromObjectAsJson(e, options);
                    if (data.ToArray().Length > MaxPayloadSizeBytes)
                    {
                        _logger.LogError("Event of type {EventType} exceeds the 1MB payload limit and will not be published.", eventType);
                        continue; // Or handle splitting/alternative logic as needed
                    }
                    var cloudEvent = new CloudEvent(
                        source.ToString(),
                        eventType,
                        data,
                        "application/json",
                        CloudEventDataFormat.Json
                    )
                    {
                        Id = Guid.NewGuid().ToString(),
                        Time = DateTimeOffset.UtcNow
                    };
                    cloudEvent.ExtensionAttributes.Add("topic", topicName);
                    allCloudEvents.Add(cloudEvent);
                }

                // Split into batches respecting Event Grid limits (count and size)
                var batches = SplitIntoBatches(allCloudEvents);

                int totalSent = 0;
                int batchNumber = 0;
                foreach (var batch in batches)
                {
                    batchNumber++;
                    int retryCount = 0;
                    const int maxRetries = 3;
                    bool sent = false;
                    while (!sent && retryCount < maxRetries)
                    {
                        try
                        {
                            await _client.SendEventsAsync(batch, cancellationToken);
                            totalSent += batch.Count;
                            _logger.LogInformation("Published batch {BatchNumber}/{TotalBatches} with {Count} events of type {EventType}",
                                batchNumber, batches.Count, batch.Count, eventType);
                            sent = true;
                        }
                        catch (Exception ex)
                        {
                            retryCount++;
                            _logger.LogError(ex, "Failed to publish batch {BatchNumber}/{TotalBatches} (attempt {Retry}) with {Count} events of type {EventType}",
                                batchNumber, batches.Count, retryCount, batch.Count, eventType);
                            if (retryCount >= maxRetries)
                            {
                                // Continue with other batches but record the failure
                                break;
                            }
                            await Task.Delay((int)(1000 * Math.Pow(2, retryCount)), cancellationToken); // Exponential backoff
                        }
                    }
                }

                _logger.LogInformation("Published {TotalSent}/{TotalEvents} events of type {EventType} in {BatchCount} batches",
                    totalSent, eventsList.Count, eventType, batches.Count);

                if (totalSent < eventsList.Count)
                {
                    throw new EventPublishException($"Failed to publish all events. Only {totalSent} out of {eventsList.Count} were sent.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish events of type {EventType}", typeof(T).Name);
                throw;
            }
        }

        // Market data changed/created "legacy" style event for IMarketData, with subject & event type logic
        public async Task PublishMarketDataEventAsync<T>(T marketData, CancellationToken cancellationToken = default) where T : IMarketDataEntity
        {
            if (marketData == null)
                throw new ArgumentNullException(nameof(marketData));

            var eventType = (marketData.Version.GetValueOrDefault() > 1)
                ? "vv.DataChanged"
                : "vv.DataCreated";

            var eventData = new BinaryData(JsonSerializer.Serialize(marketData));
            var eventGridEvent = new EventGridEvent(
                subject: $"{marketData.DataType}.{marketData.AssetClass}/{marketData.AssetId}",
                eventType: eventType,
                dataVersion: marketData.SchemaVersion,
                data: eventData
            );

            try
            {
                await _client.SendEventAsync(eventGridEvent, cancellationToken);
                _logger.LogInformation("Published EventGridEvent {EventType} for {Subject}", eventType, eventGridEvent.Subject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish EventGridEvent {EventType} for {Subject}",
                    eventType, eventGridEvent.Subject);
                throw;
            }
        }

        // Helper: PascalCase to kebab-case for topic derivation
        private static string DeriveTopicFromEventType(string eventType)
        {
            return string.Concat(eventType.Select((x, i) => i > 0 && char.IsUpper(x) ? "-" + x.ToString().ToLower() : x.ToString().ToLower()));
        }

        // Helper: Build a proper URI for the CloudEvent source
        private Uri BuildSourceUri(string topic)
        {
            // If the topic is already a valid absolute URI, use it directly
            if (Uri.TryCreate(topic, UriKind.Absolute, out var uri))
            {
                return uri;
            }

            // Normalize the topic name for use in a URI
            var normalizedTopic = topic.Replace(" ", "-").ToLowerInvariant();

            // Ensure no double slashes in the URL by trimming slashes
            var baseUri = _baseSourceUri.TrimEnd('/');
            normalizedTopic = normalizedTopic.TrimStart('/');

            // Always create a valid absolute URI with the base source URI
            return new Uri($"{baseUri}/{normalizedTopic}");
        }

        // Helper: Split CloudEvents into right-sized batches
        private List<List<CloudEvent>> SplitIntoBatches(List<CloudEvent> events)
        {
            var result = new List<List<CloudEvent>>();
            var currentBatch = new List<CloudEvent>();
            long currentBatchSize = 0;

            foreach (var cloudEvent in events)
            {
                // Estimate the size of this event
                // This is an approximation since the actual wire size includes headers, etc.
                long eventSize = EstimateEventSize(cloudEvent);

                // If adding this event would exceed either limit, start a new batch
                if (currentBatch.Count >= MaxEventsPerBatch ||
                    (currentBatch.Count > 0 && currentBatchSize + eventSize > MaxPayloadSizeBytes))
                {
                    result.Add(currentBatch);
                    currentBatch = new List<CloudEvent>();
                    currentBatchSize = 0;
                }

                currentBatch.Add(cloudEvent);
                currentBatchSize += eventSize;
            }

            // Add the last batch if it has any events
            if (currentBatch.Count > 0)
            {
                result.Add(currentBatch);
            }

            return result;
        }

        // Helper: Estimate the size of a CloudEvent (rough approximation)
        private long EstimateEventSize(CloudEvent cloudEvent)
        {
            // Base size for id, source, type, etc.
            long size = 200;

            // Add size of data
            if (cloudEvent.Data != null)
            {
                size += cloudEvent.Data.ToArray().Length;
            }

            // Add size for extension attributes
            foreach (var attr in cloudEvent.ExtensionAttributes)
            {
                size += attr.Key.Length + EstimateExtensionAttributeSize(attr.Value);
            }

            return size;
        }

        // Helper: Estimate size of extension attribute value
        private long EstimateExtensionAttributeSize(object? value)
        {
            if (value == null) return 4; // "null"
            return value.ToString()?.Length ?? 0;
        }
    }

    // Custom exception for partial publishing failures
    public class EventPublishException : Exception
    {
        public EventPublishException(string message) : base(message) { }
        public EventPublishException(string message, Exception inner) : base(message, inner) { }
    }
}