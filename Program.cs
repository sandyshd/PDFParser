using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Bcpg.OpenPgp;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Add services to the container.
builder.Services.AddControllersWithViews();

// Register BlobServiceClient for identity-based authentication
var storageAccountUrl = new Uri("https://glenfarnedemostorage1.blob.core.windows.net");
//Environment.GetEnvironmentVariable("AzureWebJobsStorage__blobServiceUri")
var blobServiceClient = new BlobServiceClient(storageAccountUrl, new DefaultAzureCredential());
builder.Services.AddSingleton(x => blobServiceClient);

var host = builder.Build();

// Resolve logger
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

// Log to check Azure Storage account connection
try
{
    await foreach (var container in blobServiceClient.GetBlobContainersAsync())
    {
        // Just enumerate one to confirm connection
        break;
    }
    Console.WriteLine("Azure Storage account connection successful.");
    logger.LogInformation("Azure Storage account connection successful.");
}
catch (Exception ex)
{
    Console.WriteLine($"Azure Storage account connection failed: {ex.Message}");
    logger.LogError(ex, "Azure Storage account connection failed: {Message}", ex.Message);
}

await host.RunAsync();
