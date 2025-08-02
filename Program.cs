using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pine.Core;
using Pine.Platform.WebService;
using System;
using System.IO;
using System.Threading;

public class Program
{
    public static async System.Threading.Tasks.Task Main(string[] args)
    {
        var webServiceDict =
            DotNetAssembly.LoadDirectoryFilesFromManifestEmbeddedFileProviderAsDictionary(
                directoryPath: ["web-service"],
                assembly: typeof(Program).Assembly)
            .Extract(err => throw new Exception($"Failed to load web service files: {err}"));

        var webServiceComposition =
            PineValueComposition.SortedTreeFromSetOfBlobsWithStringPath(webServiceDict);

        var root =
            Environment.GetEnvironmentVariable("HOME")
            ??
            Environment.GetEnvironmentVariable("HOME_EXPANDED")
            ??
            Environment.GetEnvironmentVariable("USERPROFILE")
            ??
            Environment.CurrentDirectory;

        var appStatePath = Path.Combine(root, "app-data");

        Console.WriteLine($"Using app state path: {appStatePath}");

        Directory.CreateDirectory(appStatePath);

        var fileStore =
            new Pine.FileStoreFromSystemIOFile(
                directoryPath: appStatePath,
                retryOptions: new Pine.FileStoreFromSystemIOFile.FileStoreRetryOptions
                (
                    MaxRetryAttempts: 3,
                    InitialRetryDelay: TimeSpan.FromMilliseconds(400),
                    MaxRetryDelay: TimeSpan.FromSeconds(4)
                )
            );

        static void LogMessageFromWebService(string message)
        {
            Console.WriteLine($"[{DateTime.UtcNow:O}] web servie: {message}");
        }

        var cancellationTokenSource = new CancellationTokenSource();

        var webService =
            new StaticAppSnapshottingViaJson(
                webServiceAppSourceFiles: webServiceComposition,
                fileStore: fileStore,
                logMessage: LogMessageFromWebService,
                cancellationToken: cancellationTokenSource.Token);

        var appBuilder = WebApplication.CreateBuilder(args);

        appBuilder.Services.AddLogging(logging =>
        {
            logging.AddConsole();
            logging.AddDebug();
        });

        appBuilder.WebHost
            .UseKestrel()
            .UseUrls("http://localhost:5000");

        var app = appBuilder.Build();

        // app.MapGet("/", () => "Hello World!");

        app.Run(context =>
        {
            return webService.HandleRequestAsync(context, LogMessageFromWebService);
        });

        await app.StartAsync(cancellationTokenSource.Token)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    Console.WriteLine($"Error during startup: {task.Exception?.GetBaseException().Message}");
                }
                else
                {
                    Console.WriteLine("Application started successfully.");
                }
            });

        await
            app.WaitForShutdownAsync(cancellationTokenSource.Token)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    Console.WriteLine($"Error during shutdown: {task.Exception?.GetBaseException().Message}");
                }
                else
                {
                    Console.WriteLine("Application shutdown completed successfully.");
                }
            });

        cancellationTokenSource.Cancel();
    }
}
