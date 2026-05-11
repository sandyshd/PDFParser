# PDFParser

An Azure Functions (.NET 8, isolated worker) application that automatically splits PDF files uploaded to Azure Blob Storage into:

- **Chunked text** (JSON file containing word-bounded text segments per page).
- **Extracted images** (PNG blobs grouped by source PDF).

It is designed as a pre-processing step for downstream AI / RAG (Retrieval-Augmented Generation) workloads, where PDFs need to be normalized into smaller text chunks and accompanying images.

---

> ## ⚠️ Disclaimer — Not Production Ready
>
> **This code is provided as a demo / reference implementation only. It is NOT production-ready.**
>
> Before using any part of this project in a production environment, you **must** review and adapt it to fit your business, security, compliance, and operational requirements. In particular, review (at minimum):
>
> - **Security**: authentication, authorization, secret/key management, network isolation (private endpoints, VNet integration), least-privilege RBAC on the storage account, and input validation.
> - **Reliability**: error handling, retries, poison-message handling, idempotency, and partial-failure recovery (e.g. when image upload succeeds but text upload fails).
> - **Performance & scale**: large PDF handling, memory usage (the entire PDF is processed in-memory), Functions plan sizing, concurrency limits, and throughput testing.
> - **Cost**: storage egress, Application Insights ingestion, Functions execution, and image deduplication.
> - **Data handling & compliance**: PII/PHI handling, data residency, encryption at rest/in transit, retention policies, and audit logging.
> - **Third-party licensing**: `iText7` is **AGPL-licensed**. Using it in a closed-source/commercial product requires a commercial license from iText. Replace it or obtain a license before shipping.
> - **Code quality**: the image listener uses `GetAwaiter().GetResult()` inside a synchronous interface (potential deadlocks/blocking), the storage account URL is hard-coded in [Program.cs](Program.cs), there are no unit tests, and there is no schema validation on the emitted JSON.
>
> Use at your own risk. The authors and Microsoft provide no warranties of any kind.

---

## Architecture

```
┌────────────────────────┐      ┌──────────────────────────┐      ┌──────────────────────────────┐
│  Blob upload           │      │  PdfSplitFunction        │      │  Output blobs                │
│  data/raw_data/*.pdf   │ ───► │  (Blob Trigger, .NET 8)  │ ───► │  prepaired_data/text/*.json  │
│                        │      │  iText7 + Azure SDK      │      │  prepaired_data/image/*.png  │
└────────────────────────┘      └──────────────────────────┘      └──────────────────────────────┘
```

- **Trigger**: `BlobTrigger` on `data/raw_data/{name}.pdf` using the `AzureWebJobsStorage` connection.
- **Auth**: Storage access uses `DefaultAzureCredential` (managed identity in Azure, developer credentials locally).

---

## Project Structure

| File | Purpose |
|------|---------|
| [PdfSplitFunction.cs](PdfSplitFunction.cs) | Blob-triggered function that splits PDFs into text chunks and extracts images. |
| [Program.cs](Program.cs) | Functions host bootstrap; registers `BlobServiceClient` with `DefaultAzureCredential`. |
| [PDFParser.csproj](PDFParser.csproj) | Project file and NuGet package references. |
| [host.json](host.json) | Functions host configuration (logging / Application Insights sampling). |

---

## How it works

1. A PDF is uploaded to the `data` container under the prefix `raw_data/`.
2. `PdfSplitFunction` is triggered with the blob stream.
3. For each page, the function:
   - Extracts text using `iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor`.
   - Splits the text into word-bounded chunks of `PAGE_TEXT_CHUNK_WORD_SIZE` words.
   - Walks the page content stream with a custom `ImageRenderListener` and uploads any image larger than `MIN_IMAGE_SIZE` bytes as a PNG blob.
   - Records each chunk along with the URLs of images found on that page.
4. The aggregated list is serialized to JSON and uploaded to `prepaired_data/text/{pdf-name}.json`.

### Output JSON shape

```json
[
  {
    "content": {
      "chunk": "first 200 words of the page ...",
      "imgurl": [
        "https://<account>.blob.core.windows.net/data/prepaired_data/image/<pdf-name>/image_1_1.png"
      ]
    }
  }
]
```

> Note: the same `imgurl` array is repeated for every text chunk produced from the same page.

---

## Configuration

The function reads the following environment variables (App Settings in Azure). All have defaults so the function will run without explicit configuration.

| Setting | Default | Description |
|---------|---------|-------------|
| `AzureWebJobsStorage` | _(required)_ | Connection used by the blob trigger. Use a connection string locally or `AzureWebJobsStorage__blobServiceUri` + managed identity in Azure. |
| `BLOB_CONTAINER_NAME` | `data` | Destination container for output blobs. |
| `BLOB_TEXT_DIR_FILE_PREFIX` | `prepaired_data/text` | Virtual folder prefix for output JSON files. |
| `BLOB_IMAGES_DIR_FILE_PREFIX` | `prepaired_data/image` | Virtual folder prefix for extracted images. |
| `PAGE_TEXT_CHUNK_WORD_SIZE` | `200` | Number of words per text chunk. |
| `MIN_IMAGE_SIZE` | `2048` | Minimum size in bytes for an extracted image to be uploaded. |

> ⚠️ The storage account URL used for `BlobServiceClient` is currently **hard-coded** in [Program.cs](Program.cs) (`https://glenfarnedemostorage1.blob.core.windows.net`). Move this to configuration before deploying to any other environment.

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- An Azure Storage account (or [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) for local development)
- Azure CLI (`az login`) for local credential resolution via `DefaultAzureCredential`

---

## Local development

1. Restore and build:

   ```powershell
   dotnet restore
   dotnet build
   ```

2. Create a `local.settings.json` (not committed) similar to:

   ```json
   {
     "IsEncrypted": false,
     "Values": {
       "AzureWebJobsStorage": "UseDevelopmentStorage=true",
       "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
       "BLOB_CONTAINER_NAME": "data",
       "BLOB_TEXT_DIR_FILE_PREFIX": "prepaired_data/text",
       "BLOB_IMAGES_DIR_FILE_PREFIX": "prepaired_data/image",
       "PAGE_TEXT_CHUNK_WORD_SIZE": "200",
       "MIN_IMAGE_SIZE": "2048"
     }
   }
   ```

3. Sign in so `DefaultAzureCredential` can resolve a token for the storage account:

   ```powershell
   az login
   ```

4. Run the function host:

   ```powershell
   func start
   ```

5. Upload a PDF to the `data/raw_data/` virtual folder of the configured storage account to trigger the function.

---

## Deploying to Azure

A typical deployment requires:

1. **Azure resources**:
   - Function App (Linux or Windows, .NET 8 isolated)
   - Storage Account (the one watched by the trigger and written to)
   - Application Insights (optional but recommended)

2. **Identity & RBAC**:
   - Enable a system-assigned (or user-assigned) managed identity on the Function App.
   - Grant it the following roles on the storage account:
     - `Storage Blob Data Contributor` (read PDFs, write outputs)
     - `Storage Account Contributor` (only if the function needs to create containers — otherwise pre-create them)

3. **App settings**:
   - `AzureWebJobsStorage__blobServiceUri = https://<account>.blob.core.windows.net`
   - `AzureWebJobsStorage__credential = managedidentity`
   - All `BLOB_*` and chunking settings listed above.
   - `APPLICATIONINSIGHTS_CONNECTION_STRING` for telemetry.

4. **Publish**:

   ```powershell
   func azure functionapp publish <your-function-app-name>
   ```

   …or deploy via GitHub Actions / Azure DevOps / `azd`.

> Remember to update the hard-coded storage URL in [Program.cs](Program.cs) before deploying to any environment other than the original demo.

---

## Dependencies

Key NuGet packages (see [PDFParser.csproj](PDFParser.csproj) for the full list):

- `Microsoft.Azure.Functions.Worker` (isolated worker model)
- `Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs`
- `Azure.Storage.Blobs` / `Azure.Identity`
- `itext7` — PDF parsing **(AGPL — see disclaimer above)**
- `Microsoft.ApplicationInsights.WorkerService`

---

## Known limitations

- Entire PDF is loaded into memory; very large PDFs may exhaust memory on Consumption plans.
- The output JSON repeats the per-page image list for every text chunk on that page.
- Image extraction relies on raw image XObjects in the PDF; vector graphics and rasterized page content are not captured.
- `ImageRenderListener.EventOccurred` blocks on async upload via `GetAwaiter().GetResult()` — fine for demos, problematic under load.
- No deduplication of identical images across pages.
- No retry / poison-blob handling beyond the Functions host defaults.
- Output path uses the literal string `prepaired_data` (typo of "prepared") for backward compatibility with the original demo.

---

## License

No license is included. Treat the source as **all rights reserved** unless your organization adds an explicit license. Note that distributing this code while linked against `iText7` requires complying with the AGPL or holding a commercial iText license.
