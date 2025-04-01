# Azure AI SQL Indexer

A .NET console application that reads records from a SQL table, generates AI-powered summaries and embeddings using Azure OpenAI, and indexes them into Azure Cognitive Search.

## Overview

This tool bridges structured SQL data with Azure AI Search by:

1. Connecting to any SQL table and retrieving records
2. Generating concise natural language summaries using Azure OpenAI
3. Producing vector embeddings for semantic search
4. Automatically building and updating a search index schema
5. Uploading searchable, vectorised records into Azure Cognitive Search

Ideal for enriching internal datasets with semantic capabilities for Gen AI applications.

## Features

- **Interactive Console**: User-friendly prompts for all required configurations
- **Flexible SQL Input**: Supports any table and schema with optional primary key
- **AI-Powered Summarisation**: Generates British English summaries via GPT
- **Embedding Generation**: Uses Azure OpenAI for high-dimension vectors
- **Automatic Schema Detection**: Index schema derived from actual data
- **Semantic & Vector Search Support**: Ready for hybrid search experiences

## Requirements

- .NET 9.0 or newer
- Azure Cognitive Search instance (with Admin API access)
- Azure OpenAI resource with deployment for GPT and embeddings
- SQL Server (local or remote)

## Installation

1. Clone this repository
2. Open the solution in Visual Studio 2022 or later
3. Restore NuGet packages
4. Build the solution (Ctrl+Shift+B)

## Usage

### 1. Run the Application

```bash
dotnet run
```

Follow the prompts in the console:

```text
Enter SQL Connection String:
Enter SQL Table Name:
Enter the Primary Key Column Name (leave blank to auto-generate IDs):

Enter Azure Cognitive Search Service Endpoint:
Enter Azure Cognitive Search Admin API Key:
Enter Azure Cognitive Search Index Name:

Enter Azure OpenAI Endpoint:
Choose Azure OpenAI Authentication Method:
  1. API Key
  2. Microsoft Entra Credential (DefaultAzureCredential)
Enter Azure OpenAI Deployment Name (for text generation):
Enter Azure OpenAI Text Embedding Deployment Name:
```

### 2. Output

Once complete, you'll see:

- Number of SQL records retrieved and summarised
- Embeddings generated
- Index created/updated
- Documents indexed successfully

## Configuration Details

### Text Representation

- The app uses Azure OpenAI Chat Completion to create a natural language summary of each SQL record.
- Prompts are structured to encourage readable and concise British English output.

### Embedding Vector

- Embeddings are generated using Semantic Kernel and Azure OpenAI’s embedding API.
- The generated vector is stored in the `contentVector` field (dimension: 3072).

### Index Schema

- Schema is derived from actual SQL data.
- Adds default fields: `textRepresentation`, `contentVector`, and `id` (if no primary key).
- Configured for both semantic and vector search using HNSW and cosine similarity.

## Code Structure

- **Program.cs** – Orchestrates user input, OpenAI processing, and Azure Search operations
- **SqlHelper** – Reads top 1000 records from specified table
- **DynamicTextRepresentationGenerator** – Summarises each record with GPT
- **KernelEmbeddingHelper** – Generates embeddings using Semantic Kernel
- **AzureSearchIndexer** – Creates index and uploads documents

## Troubleshooting

### Nothing Indexed

- Ensure the SQL query is returning records
- Verify API key and endpoint for Azure Search are correct
- Check Azure OpenAI deployment names are valid

### Text Representation Fails

- If the GPT prompt fails, fallback text will be used
- Review console logs for error messages

### Indexing Errors

- Confirm index name is unique or delete the existing index manually
- Check vector dimension matches embedding model output

## Future Enhancements

- Config file support for non-interactive runs
- Chunking for large records or long text fields
- Integration with Azure Blob Storage or CosmosDB
- Language detection and multi-lingual summarisation
- Logging to Application Insights or file system

## License

This project is licensed for internal use only. All rights reserved.

## Contributors

- Sulyman Alani

---

*For questions or support, contact Sulyman Alani.*
