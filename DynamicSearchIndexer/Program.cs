using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;

namespace DynamicSearchIndexer
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // -------------------------------
            // User Input for SQL Configuration
            // -------------------------------
            Console.WriteLine("Enter SQL Connection String:");
            string sqlConnectionString = Console.ReadLine();

            Console.WriteLine("Enter SQL Table Name:");
            string tableName = Console.ReadLine();

            Console.WriteLine("Enter the Primary Key Column Name (leave blank to auto-generate IDs):");
            string primaryKeyColumn = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(primaryKeyColumn))
            {
                primaryKeyColumn = primaryKeyColumn.Trim();
            }

            // -------------------------------
            // User Input for Azure Cognitive Search
            // -------------------------------
            Console.WriteLine("Enter Azure Cognitive Search Service Endpoint (e.g., https://your-search-service.search.windows.net):");
            string searchServiceEndpoint = Console.ReadLine();

            Console.WriteLine("Enter Azure Cognitive Search Admin API Key:");
            string searchAdminApiKey = Console.ReadLine();

            Console.WriteLine("Enter Azure Cognitive Search Index Name:");
            string indexName = Console.ReadLine();

            // -------------------------------
            // User Input for Azure OpenAI Configuration
            // -------------------------------
            Console.WriteLine("Enter Azure OpenAI Endpoint (e.g., https://your-openai-resource.openai.azure.com/):");
            string openAiEndpointString = Console.ReadLine();
            Uri openAiEndpoint = new Uri(openAiEndpointString);

            Console.WriteLine("Choose Azure OpenAI Authentication Method:");
            Console.WriteLine("  1. API Key");
            Console.WriteLine("  2. Microsoft Entra Credential (DefaultAzureCredential)");
            string authChoice = Console.ReadLine();

            AzureOpenAIClient azureOpenAiClient;
            string openAiApiKey = null;
            if (authChoice.Trim() == "1")
            {
                Console.WriteLine("Enter Azure OpenAI API Key:");
                openAiApiKey = Console.ReadLine();
                azureOpenAiClient = new AzureOpenAIClient(openAiEndpoint, new ApiKeyCredential(openAiApiKey));
            }
            else
            {
                // Use DefaultAzureCredential for secure keyless authentication.
                azureOpenAiClient = new AzureOpenAIClient(openAiEndpoint, new DefaultAzureCredential());
            }

            Console.WriteLine("Enter Azure OpenAI Deployment Name (for text generation):");
            string openAiDeploymentName = Console.ReadLine();

            Console.WriteLine("Enter Azure OpenAI Text Embedding Deployment Name:");
            string openAiTextEmbeddingDeploymentName = Console.ReadLine();

            // -------------------------------
            // Retrieve SQL Records
            // -------------------------------
            var records = await SqlHelper.GetRecordsAsync(sqlConnectionString, tableName);
            Console.WriteLine($"Retrieved {records.Count} records from table '{tableName}'.");

            // -------------------------------
            // Initialize AI Text Generator
            // -------------------------------
            var textGenerator = new DynamicTextRepresentationGenerator(azureOpenAiClient, openAiDeploymentName);

            // -------------------------------
            // Process Records, Generate Embeddings & Build Documents
            // -------------------------------
            var documents = new List<SearchDocument>();
            foreach (var record in records)
            {
                // Convert the primary key value to string if applicable.
                if (!string.IsNullOrWhiteSpace(primaryKeyColumn) && record.ContainsKey(primaryKeyColumn))
                {
                    record[primaryKeyColumn] = record[primaryKeyColumn]?.ToString();
                }

                // Ensure all values in the record are primitive types.
                foreach (var key in record.Keys.ToList())
                {
                    var value = record[key];
                    if (value != null && !(value is string || value is int || value is long ||
                        value is double || value is float || value is decimal || value is bool || value is DateTime))
                    {
                        // For non-primitive values, convert to string.
                        record[key] = value.ToString();
                    }
                }

                // Generate a dynamic text representation for each record.
                string textRepresentation = await textGenerator.GenerateDynamicTextRepresentationAsync(record);

                // Generate vector embeddings using our KernelEmbeddingHelper.
                float[] embeddingVector = await KernelEmbeddingHelper.GenerateEmbeddingForTextAsync(
                    textRepresentation, openAiEndpoint.ToString(), openAiTextEmbeddingDeploymentName, openAiApiKey);

                // Use the provided primary key column if available; otherwise, generate a new GUID.
                string docKey = (!string.IsNullOrWhiteSpace(primaryKeyColumn) && record.ContainsKey(primaryKeyColumn))
                                ? record[primaryKeyColumn]?.ToString()
                                : Guid.NewGuid().ToString();

                // Create a SearchDocument from the record.
                var document = new SearchDocument(record);

                // Set the primary key field using the correct property name.
                if (!string.IsNullOrWhiteSpace(primaryKeyColumn))
                {
                    document[primaryKeyColumn] = docKey;
                }
                else
                {
                    document["id"] = docKey;
                }

                // Add additional fields.
                document["textRepresentation"] = textRepresentation;
                document["contentVector"] = embeddingVector;

                documents.Add(document);
            }
            Console.WriteLine($"Processed {documents.Count} records with text representations and embeddings.");

            // -------------------------------
            // Create/Update Azure Search Index (with vector and semantic configuration)
            // -------------------------------
            await AzureSearchIndexer.CreateOrUpdateIndexAsync(
                searchServiceEndpoint,
                searchAdminApiKey,
                indexName,
                records,
                primaryKeyColumn
            );
            Console.WriteLine("Search index created or updated successfully.");

            // -------------------------------
            // Index Documents
            // -------------------------------
            await AzureSearchIndexer.IndexDocumentsAsync(searchServiceEndpoint, searchAdminApiKey, indexName, documents);
            Console.WriteLine("Documents have been indexed successfully.");
        }
    }

    /// <summary>
    /// Helper class to retrieve records from a SQL table.
    /// </summary>
    public static class SqlHelper
    {
        public static async Task<List<Dictionary<string, object>>> GetRecordsAsync(string connectionString, string tableName)
        {
            var records = new List<Dictionary<string, object>>();
            string query = $"SELECT TOP 1000 * FROM {tableName}";

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand(query, connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var record = new Dictionary<string, object>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            record[reader.GetName(i)] = reader.GetValue(i);
                        }
                        records.Add(record);
                    }
                }
            }
            return records.Take(5).ToList();
        }
    }

    /// <summary>
    /// Uses AzureOpenAIClient to generate a dynamic text representation for a record.
    /// </summary>
    public class DynamicTextRepresentationGenerator
    {
        private readonly AzureOpenAIClient _azureOpenAiClient;
        private readonly string _deploymentName;

        public DynamicTextRepresentationGenerator(AzureOpenAIClient azureOpenAiClient, string deploymentName)
        {
            _azureOpenAiClient = azureOpenAiClient;
            _deploymentName = deploymentName;
        }

        /// <summary>
        /// Generates a natural language summary from a record.
        /// </summary>
        /// <param name="record">A dictionary representing a record from the SQL table.</param>
        /// <returns>A text summary suitable for search indexing.</returns>
        public async Task<string> GenerateDynamicTextRepresentationAsync(Dictionary<string, object> record)
        {
            var prompt = new StringBuilder();
            prompt.AppendLine("Generate a concise, informative text representation for the following record:");
            prompt.AppendLine("---------------------------------------------------------");

            foreach (var kvp in record)
            {
                string value = kvp.Value?.ToString() ?? string.Empty;
                prompt.AppendLine($"{kvp.Key}: {value}");
            }
            prompt.AppendLine("---------------------------------------------------------");
            prompt.AppendLine("The summary should be written in clear British English and suitable for use in a search index.");

            try
            {
                ChatClient chatClient = _azureOpenAiClient.GetChatClient(_deploymentName);
                ChatCompletion completion = await chatClient.CompleteChatAsync(new ChatMessage[]
                {
                    new SystemChatMessage("You are a helpful assistant that generates concise record summaries in British English."),
                    new UserChatMessage(prompt.ToString())
                });

                var summaryText = completion.Content[0].Text.Trim();
                return string.IsNullOrWhiteSpace(summaryText)
                    ? "No summary generated for the record."
                    : summaryText;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating text representation: {ex.Message}");
                return "Error generating text representation.";
            }
        }
    }

    /// <summary>
    /// Helper class for Azure Cognitive Search operations: index creation/update and document indexing.
    /// </summary>
    public static class AzureSearchIndexer
    {
        /// <summary>
        /// Creates or updates an index with a dynamic schema based on the first record's columns,
        /// including vector search and semantic configuration.
        /// </summary>
        public static async Task CreateOrUpdateIndexAsync(string searchServiceEndpoint, string adminApiKey, string indexName, List<Dictionary<string, object>> records, string primaryKeyColumn)
        {
            var serviceEndpoint = new Uri(searchServiceEndpoint);
            var credential = new AzureKeyCredential(adminApiKey);
            var indexClient = new SearchIndexClient(serviceEndpoint, credential);

            var fields = new List<SearchField>();

            // Use the user-specified primary key if provided; otherwise, use a default "id" field.
            if (!string.IsNullOrWhiteSpace(primaryKeyColumn))
            {
                fields.Add(new SearchField(primaryKeyColumn, SearchFieldDataType.String)
                {
                    IsKey = true,
                    IsFilterable = true
                });
            }
            else
            {
                fields.Add(new SearchField("id", SearchFieldDataType.String)
                {
                    IsKey = true,
                    IsFilterable = true
                });
            }

            // Always add the AI-generated text representation field.
            fields.Add(new SearchField("textRepresentation", SearchFieldDataType.String)
            {
                IsSearchable = true
            });

            // Add the vector field for embeddings.
            // Updated dimension to 3072 to match the generated embeddings.
            fields.Add(new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                VectorSearchDimensions = 3072,
                VectorSearchProfileName = "vector-profile",
                IsSearchable = true
            });

            // Dynamically add additional fields based on the first record's schema.
            if (records != null && records.Count > 0)
            {
                var firstRecord = records[0];
                foreach (var kvp in firstRecord)
                {
                    if ((!string.IsNullOrWhiteSpace(primaryKeyColumn) && kvp.Key.Equals(primaryKeyColumn, System.StringComparison.OrdinalIgnoreCase)) ||
                        (string.IsNullOrWhiteSpace(primaryKeyColumn) && kvp.Key.Equals("id", System.StringComparison.OrdinalIgnoreCase)) ||
                        kvp.Key.Equals("textRepresentation", System.StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.Equals("contentVector", System.StringComparison.OrdinalIgnoreCase))
                        continue;

                    SearchFieldDataType fieldType = SearchFieldDataType.String;
                    if (kvp.Value is int || kvp.Value is long)
                        fieldType = SearchFieldDataType.Int64;
                    else if (kvp.Value is decimal || kvp.Value is float || kvp.Value is double)
                        fieldType = SearchFieldDataType.Double;
                    else if (kvp.Value is DateTime)
                        fieldType = SearchFieldDataType.DateTimeOffset;
                    else if (kvp.Value is bool)
                        fieldType = SearchFieldDataType.Boolean;

                    fields.Add(new SearchField(kvp.Key, fieldType)
                    {
                        IsSearchable = fieldType == SearchFieldDataType.String,
                        IsFilterable = true,
                        IsSortable = fieldType != SearchFieldDataType.String,
                        IsFacetable = fieldType != SearchFieldDataType.String
                    });
                }
            }

            var index = new SearchIndex(indexName)
            {
                Fields = fields
            };

            // Configure vector search.
            index.VectorSearch = new VectorSearch();
            index.VectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("hnsw-config")
            {
                Name = "hnsw-config",
                Parameters = new HnswParameters
                {
                    M = 4,
                    EfConstruction = 400,
                    EfSearch = 500,
                    Metric = VectorSearchAlgorithmMetric.Cosine
                },
            });
            index.VectorSearch.Profiles.Add(new VectorSearchProfile("vector-profile", "hnsw-config"));

            await indexClient.CreateOrUpdateIndexAsync(index);
        }

        /// <summary>
        /// Indexes the list of documents into the specified search index.
        /// </summary>
        public static async Task IndexDocumentsAsync(string searchServiceEndpoint, string adminApiKey, string indexName, List<SearchDocument> documents)
        {
            var serviceEndpoint = new Uri(searchServiceEndpoint);
            var credential = new AzureKeyCredential(adminApiKey);
            var searchClient = new SearchClient(serviceEndpoint, indexName, credential);

            var batch = IndexDocumentsBatch.Create(
                documents.ConvertAll(doc => IndexDocumentsAction.Upload(doc)).ToArray()
            );

            await searchClient.IndexDocumentsAsync(batch);
        }
    }

    /// <summary>
    /// A helper class that creates a Semantic Kernel instance, registers the embedding service,
    /// and generates an embedding for a given text, returning it as a float array.
    /// </summary>
    public static class KernelEmbeddingHelper
    {
        /// <summary>
        /// Creates a kernel with the Azure OpenAI text embedding service and generates an embedding for the provided text.
        /// </summary>
        /// <param name="text">The text to embed.</param>
        /// <param name="openAiEndpoint">The Azure OpenAI endpoint (e.g., "https://your-openai-resource.openai.azure.com/").</param>
        /// <param name="deploymentName">The deployment name for text embeddings.</param>
        /// <param name="openAiApiKey">
        /// The API key for Azure OpenAI. If null or empty, DefaultAzureCredential is used.
        /// </param>
        /// <returns>A float array representing the embedding vector.</returns>
        public static async Task<float[]> GenerateEmbeddingForTextAsync(string text, string openAiEndpoint, string deploymentName, string? openAiApiKey = null)
        {
            var services = new ServiceCollection();

#pragma warning disable SKEXP0010 // Type is for evaluation purposes only and is subject to change or removal in future updates.
            services.AddAzureOpenAITextEmbeddingGeneration(deploymentName: deploymentName, endpoint: openAiEndpoint, apiKey: openAiApiKey);
#pragma warning restore SKEXP0010

            // Create the kernel builder and build a kernel.
            var builder = Kernel.CreateBuilder();
            var serviceProvider = services.BuildServiceProvider();
            Kernel kernel = builder.Build();

#pragma warning disable SKEXP0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
            var embeddingService = serviceProvider.GetRequiredService<ITextEmbeddingGenerationService>();
#pragma warning restore SKEXP0001

            ReadOnlyMemory<float> embeddingMemory = await embeddingService.GenerateEmbeddingAsync(text);
            return embeddingMemory.ToArray();
        }
    }
}
