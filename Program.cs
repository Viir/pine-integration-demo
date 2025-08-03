using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pine.Core;
using Pine.Core.PopularEncodings;
using Pine.Elm.Platform;
using Pine.Platform.WebService;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

public class Program
{
    const string webServiceCompiledFileName =
        "compiled-modules";

    static readonly IReadOnlyList<string> s_webServiceCompiledDirectoryPathRelative =
        ["web-service-compiled"];

    public static async System.Threading.Tasks.Task Main(string[] args)
    {
        var webServiceDict =
            DotNetAssembly.LoadDirectoryFilesFromManifestEmbeddedFileProviderAsDictionary(
                directoryPath: ["web-service"],
                assembly: typeof(Program).Assembly)
            .Extract(err => throw new Exception($"Failed to load web service files: {err}"));

        var webServiceComposition =
            PineValueComposition.SortedTreeFromSetOfBlobsWithStringPath(webServiceDict);

        var isCommandJustBuild =
            Array.Exists(args, arg => arg.Equals("just-build", StringComparison.OrdinalIgnoreCase));

        if (isCommandJustBuild)
        {
            Console.WriteLine("Skipping web service startup due to 'just-build' command.");

            var webServiceCompiled =
                WebServiceInterface.CompiledModulesFromSourceFilesAndEntryFileName(
                    webServiceComposition,
                    entryFileName: ["src", "Backend", "Main.elm"]);

            var webServiceCompiledDirectoryPath =
                Path.Combine([Environment.CurrentDirectory, .. s_webServiceCompiledDirectoryPathRelative]);

            var webServiceCompiledFilePath =
                Path.Combine(webServiceCompiledDirectoryPath, webServiceCompiledFileName);

            Console.WriteLine($"Writing compiled web service to: {webServiceCompiledFilePath}");

            Directory.CreateDirectory(webServiceCompiledDirectoryPath);

            using var fileStream =
                new FileStream(
                    Path.Combine(webServiceCompiledFilePath),
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);

            PineValueBinaryEncoding.Encode(fileStream, webServiceCompiled);

            return;
        }

        var prebuildWebServiceDict =
            DotNetAssembly.LoadDirectoryFilesFromManifestEmbeddedFileProviderAsDictionary(
                directoryPath: s_webServiceCompiledDirectoryPathRelative,
                assembly: typeof(Program).Assembly)
            .Extract(err => throw new Exception($"Failed to load web service compiled: {err}"));

        var webServiceCompiledModulesEncoded =
            prebuildWebServiceDict[[webServiceCompiledFileName]];

        var webServiceCompiledModules =
            PineValueBinaryEncoding.DecodeRoot(webServiceCompiledModulesEncoded);

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
            StaticAppSnapshottingState.Create(
                webServiceCompiledModules: webServiceCompiledModules,
                fileStore: fileStore,
                logMessage: LogMessageFromWebService,
                cancellationToken: cancellationTokenSource.Token);

        var appBuilder = WebApplication.CreateBuilder(args);

        appBuilder.Services.AddLogging(logging =>
        {
            logging.AddConsole();
            logging.AddDebug();
        });

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
