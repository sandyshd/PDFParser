using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.IO.Image;

namespace PDFParser
{
    public class PdfSplitFunction
    {
        private readonly ILogger<PdfSplitFunction> _logger;
        private readonly BlobServiceClient _blobServiceClient;

        public PdfSplitFunction(ILogger<PdfSplitFunction> logger, BlobServiceClient blobServiceClient)
        {
            _logger = logger;
            _blobServiceClient = blobServiceClient;
        }

        [Function("PdfSplitFunction")]
        public async Task Run(
            [BlobTrigger("data/raw_data/{name}.pdf", Connection = "AzureWebJobsStorage")] Stream pdfStream,
            string name)
        {
            var containerName = Environment.GetEnvironmentVariable("BLOB_CONTAINER_NAME") ?? "data";
            var textDirPrefix = Environment.GetEnvironmentVariable("BLOB_TEXT_DIR_FILE_PREFIX") ?? "prepaired_data/text";
            var imageDirPrefix = Environment.GetEnvironmentVariable("BLOB_IMAGES_DIR_FILE_PREFIX") ?? "prepaired_data/image";
            var chunkSize = int.TryParse(Environment.GetEnvironmentVariable("PAGE_TEXT_CHUNK_WORD_SIZE"), out var cs) ? cs : 200;
            var minImgSize = int.TryParse(Environment.GetEnvironmentVariable("MIN_IMAGE_SIZE"), out var mi) ? mi : 2048;
            var filenameWithoutExtension = Path.GetFileNameWithoutExtension(name).Replace(" ", "-");
            var destContainer = _blobServiceClient.GetBlobContainerClient(containerName);

            await destContainer.CreateIfNotExistsAsync();
            var dataList = new List<object>();

            pdfStream.Position = 0;
            using (var pdfReader = new PdfReader(pdfStream))
            using (var pdfDoc = new PdfDocument(pdfReader))
            {
                int numberOfPages = pdfDoc.GetNumberOfPages();
                for (int pageNum = 1; pageNum <= numberOfPages; pageNum++)
                {
                    var page = pdfDoc.GetPage(pageNum);

                    // Extract text
                    var text = PdfTextExtractor.GetTextFromPage(page);
                    var wordList = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    // Extract images
                    var imageUrls = new List<string>();
                    var imageListener = new ImageRenderListener(async (imageBytes, index) =>
                    {
                        if (imageBytes.Length > minImgSize)
                        {
                            var fileName = $"image_{pageNum}_{index}.png";
                            var fullFilePath = $"{imageDirPrefix}/{filenameWithoutExtension}/{fileName}";
                            var blobClient = destContainer.GetBlobClient(fullFilePath);
                            using (var ms = new MemoryStream(imageBytes))
                            {
                                await blobClient.UploadAsync(ms, overwrite: true);
                            }
                            imageUrls.Add(blobClient.Uri.ToString());
                        }
                    });

                    var strategy = new FilteredEventListener();
                    strategy.AttachEventListener(imageListener);
                    var processor = new PdfCanvasProcessor(strategy);
                    processor.ProcessPageContent(page);

                    foreach (var chunk in ChunkWords(wordList, chunkSize))
                    {
                        dataList.Add(new { content = new { chunk, imgurl = imageUrls } });
                    }
                }
            }

            // Upload text JSON
            var textBlobPath = $"{textDirPrefix}/{filenameWithoutExtension}.json";
            var textBlobClient = destContainer.GetBlobClient(textBlobPath);
            using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dataList))))
            {
                await textBlobClient.UploadAsync(ms, overwrite: true);
            }

            _logger.LogInformation("PDF split and extraction complete for {name}", name);
        }

        private static IEnumerable<string> ChunkWords(string[] words, int chunkSize)
        {
            for (int i = 0; i < words.Length; i += chunkSize)
            {
                yield return string.Join(" ", words, i, Math.Min(chunkSize, words.Length - i));
            }
        }

        // Helper class for image extraction
        private class ImageRenderListener : IEventListener
        {
            private readonly Func<byte[], int, Task> _onImageFound;
            private int _imageIndex = 1;

            public ImageRenderListener(Func<byte[], int, Task> onImageFound)
            {
                _onImageFound = onImageFound;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type == EventType.RENDER_IMAGE)
                {
                    var renderInfo = (ImageRenderInfo)data;
                    var imageObject = renderInfo.GetImage();
                    if (imageObject != null)
                    {
                        var imageBytes = imageObject.GetImageBytes(true);
                        _onImageFound(imageBytes, _imageIndex).GetAwaiter().GetResult();
                        _imageIndex++;
                    }
                }
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return new HashSet<EventType> { EventType.RENDER_IMAGE };
            }
        }
    }
}
