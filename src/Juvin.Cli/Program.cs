// ReSharper disable MethodHasAsyncOverload -- a CLI writes to the console synchronously; nothing else is waiting
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Juvin.Core;
using Juvin.Core.Models;
using Juvin.Core.Providers;
using Juvin.Core.Rewrite;
using Juvin.Core.Services;

namespace Juvin.Cli;

public static class Program
{
    private const string Usage =
        """
        Usage: juvin-cli rewrite --stdin [options]

        Reads a draft from stdin and streams the rewrite to stdout.
        Input is JSON {"draft", "subject", "thread": [{"from", "to", "sent", "body"}]}
        or plain text, taken as the draft alone.

        Options:
          --action <id>          soften, clearer, shorter, grammar, custom, missing, or the id
                                 of an action added in Settings (default clearer)
          --instruction <text>   the instruction for the custom action
          --mode <local|codex>   processing mode for this run
          --model <id>           model for this run
          --server <url>         local server URL for this run
          --provenance           write the provenance record as JSON to stderr
          --settings <path>      settings file to read instead of the default
          --help                 show this help

        Exit codes: 0 success, 1 provider failure, 2 usage error.
        """;

    private static readonly JsonSerializerOptions s_provenanceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        var options = CliOptions.Parse(args, out var error);
        if (options is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Run juvin-cli --help for usage.");
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        RewriteContext context;
        try
        {
            context = StdinInput.Parse(await Console.In.ReadToEndAsync());
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (context.Draft.Length == 0)
        {
            Console.Error.WriteLine("The draft is empty.");
            return 2;
        }

        AppSettings settings;
        try
        {
            settings = options.ApplyTo(
                new SettingsService(options.SettingsPath ?? JuvinEnvironment.SettingsFilePath).Current
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"The settings file could not be read: {ex.Message}");
            return 2;
        }

        var action = options.ResolveAction(new RewriteActionCatalog(settings), out error);
        if (action is null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        // ReSharper disable once AccessToDisposedClosure -- Ctrl+C after Main returns has nothing left to cancel
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        var selector = ProviderSelector.CreateDefault(http, new ProcessRunner());
        RewriteSession? session = null;
        var printed = 0;
        var preview = new InlineProgress<string>(text =>
        {
            if (text.Length <= printed)
            {
                return;
            }

            Console.Out.Write(text.AsSpan(printed));
            Console.Out.Flush();
            printed = text.Length;
        });

        try
        {
            session = new RewriteSession(selector, settings, context);
            var result = await session.RunAsync(action, options.Instruction, preview, cancellation.Token);
            preview.Report(result.Text);
            Console.Out.WriteLine();
            WriteProvenance(options, session);
            return 0;
        }
        catch (ProviderException ex)
        {
            if (printed > 0)
            {
                Console.Out.WriteLine();
            }

            Console.Error.WriteLine(ex.Message);
            WriteProvenance(options, session);
            return 1;
        }
        catch (OperationCanceledException)
        {
            if (printed > 0)
            {
                Console.Out.WriteLine();
            }

            Console.Error.WriteLine("Cancelled.");
            WriteProvenance(options, session);
            return 1;
        }
    }

    private static void WriteProvenance(CliOptions options, RewriteSession? session)
    {
        if (options.Provenance && session?.LastProvenance is { } provenance)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(provenance, s_provenanceJson));
        }
    }
}
