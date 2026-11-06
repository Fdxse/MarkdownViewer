using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace MarkdownViewer.Rendering;

/// <summary>A finished Mermaid diagram: the picture plus its natural size in device-independent pixels.</summary>
/// <param name="DiagramBitmap">The rendered diagram (drawn at twice its size for sharpness).</param>
/// <param name="DisplayWidth">Width the diagram should be shown at.</param>
/// <param name="DisplayHeight">Height the diagram should be shown at.</param>
public sealed record MermaidDiagramImage(BitmapSource DiagramBitmap, double DisplayWidth, double DisplayHeight);

/// <summary>
/// Turns Mermaid diagram text (```mermaid code blocks) into pictures.
///
/// Mermaid is a JavaScript library, so this class runs it in an invisible WebView2 (the Edge engine that ships
/// with Windows 10 and 11). The browser is only started the first time a document contains a diagram.
/// mermaid.min.js and the host page are embedded in the exe and served from a fake host name, and the page
/// blocks all other requests, so nothing is downloaded and diagram text never leaves the computer.
///
/// Results are cached by diagram text, so re-rendering a document while typing only redraws changed diagrams.
/// All members must be used from the UI thread.
/// </summary>
public sealed class MermaidDiagramRenderer
{
    /// <summary>The one renderer shared by all documents (one hidden browser for the whole app).</summary>
    public static MermaidDiagramRenderer Shared { get; } = new();

    /// <summary>Fake host name the embedded files are served from. Never reaches the network.</summary>
    private const string VirtualHostAddress = "https://mermaid-host.local/";

    /// <summary>How long to wait for the hidden browser and mermaid.js to start.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for a single diagram before giving up and showing the code.</summary>
    private static readonly TimeSpan DiagramTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Upper limit for cached diagrams; the cache is emptied when it grows past this.</summary>
    private const int MaximumCachedDiagrams = 100;

    /// <summary>Finished (or running) renders keyed by the exact diagram text.</summary>
    private readonly Dictionary<string, Task<MermaidRenderOutcome>> renderTasksByDiagramText = new(StringComparer.Ordinal);

    /// <summary>Renders that are waiting for an answer from the page, keyed by request id.</summary>
    private readonly Dictionary<int, TaskCompletionSource<MermaidRenderOutcome>> pendingRequestsById = new();

    /// <summary>Completes when the page reports that mermaid.js has loaded.</summary>
    private readonly TaskCompletionSource pageReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Starts the browser once; every render awaits this. Null until the first diagram is requested.</summary>
    private Task<CoreWebView2Controller>? browserStartupTask;

    /// <summary>Id given to the next render request so answers can be matched to their diagram.</summary>
    private int nextRequestId = 1;

    private MermaidDiagramRenderer()
    {
    }

    /// <summary>
    /// Returns the finished render for this diagram text if it is already in the cache, otherwise null.
    /// Lets the document renderer show cached diagrams straight away (also when printing).
    /// </summary>
    public MermaidRenderOutcome? TryGetFinishedRender(string diagramText)
    {
        return renderTasksByDiagramText.TryGetValue(diagramText, out var renderTask) && renderTask.IsCompletedSuccessfully
            ? renderTask.Result
            : null;
    }

    /// <summary>
    /// Renders Mermaid text to a picture. Never throws: failures (syntax errors, no WebView2 runtime,
    /// time-outs) come back as an outcome with an error message, so the caller can keep showing the code.
    /// </summary>
    public Task<MermaidRenderOutcome> RenderAsync(string diagramText)
    {
        if (renderTasksByDiagramText.TryGetValue(diagramText, out var existingTask))
        {
            return existingTask;
        }

        // Simple size limit: while typing, every intermediate version of a diagram ends up in the cache.
        if (renderTasksByDiagramText.Count >= MaximumCachedDiagrams)
        {
            renderTasksByDiagramText.Clear();
        }

        var renderTask = RenderUncachedAsync(diagramText);
        renderTasksByDiagramText[diagramText] = renderTask;
        return renderTask;
    }

    /// <summary>Does the actual work for <see cref="RenderAsync"/>, turning every failure into an error outcome.</summary>
    private async Task<MermaidRenderOutcome> RenderUncachedAsync(string diagramText)
    {
        try
        {
            browserStartupTask ??= StartBrowserAsync();
            var browserController = await browserStartupTask;

            // Register the request before sending it, so a very fast answer cannot be missed.
            int requestId = nextRequestId++;
            var answerSource = new TaskCompletionSource<MermaidRenderOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingRequestsById[requestId] = answerSource;

            try
            {
                // JsonSerializer produces a correctly escaped JavaScript string literal for the diagram text.
                string scriptCall = $"renderDiagram({requestId}, {JsonSerializer.Serialize(diagramText)});";
                await browserController.CoreWebView2.ExecuteScriptAsync(scriptCall);
                return await answerSource.Task.WaitAsync(DiagramTimeout);
            }
            finally
            {
                pendingRequestsById.Remove(requestId);
            }
        }
        catch (TimeoutException)
        {
            return MermaidRenderOutcome.Failed("Drawing the diagram took too long.");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return MermaidRenderOutcome.Failed("Diagrams need the Microsoft Edge WebView2 Runtime, which is not installed.");
        }
        catch (Exception renderError)
        {
            return MermaidRenderOutcome.Failed(renderError.Message);
        }
    }

    /// <summary>
    /// Starts the invisible browser inside the main window, loads the host page and waits until mermaid.js is ready.
    /// </summary>
    private async Task<CoreWebView2Controller> StartBrowserAsync()
    {
        var mainWindow = Application.Current.MainWindow
            ?? throw new InvalidOperationException("The main window is not open yet.");
        IntPtr mainWindowHandle = new WindowInteropHelper(mainWindow).EnsureHandle();

        // Keep the browser's cache and settings in the user's local app data instead of next to the exe,
        // which may be a read-only folder such as Program Files.
        string browserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownViewer", "WebView2");

        var environmentOptions = new CoreWebView2EnvironmentOptions
        {
            // The browser sits outside the visible part of the window. Without these switches Chromium may treat it
            // as hidden and pause drawing, which would stall diagram rendering.
            AdditionalBrowserArguments =
                "--disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling --disable-renderer-backgrounding",
        };
        var browserEnvironment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: browserDataFolder, options: environmentOptions);

        var browserController = await browserEnvironment.CreateCoreWebView2ControllerAsync(mainWindowHandle);
        // Place the browser far outside the window's visible area: it must be "visible" to render, but never seen.
        browserController.Bounds = new System.Drawing.Rectangle(-20000, -20000, 1000, 800);
        browserController.IsVisible = true;

        var browserCore = browserController.CoreWebView2;
        browserCore.Settings.AreDevToolsEnabled = false;
        browserCore.Settings.AreDefaultContextMenusEnabled = false;
        browserCore.Settings.IsStatusBarEnabled = false;
        browserCore.WebMessageReceived += HandleWebMessage;

        // Answer requests for the fake host from files embedded in the exe.
        browserCore.AddWebResourceRequestedFilter(VirtualHostAddress + "*", CoreWebView2WebResourceContext.All);
        browserCore.WebResourceRequested += (_, requestDetails) =>
            requestDetails.Response = CreateEmbeddedFileResponse(browserEnvironment, requestDetails.Request.Uri);

        // Close the browser processes together with the app.
        Application.Current.Exit += (_, _) => browserController.Close();

        browserCore.Navigate(VirtualHostAddress + "MermaidHost.html");
        await pageReadySignal.Task.WaitAsync(StartupTimeout);

        return browserController;
    }

    /// <summary>Builds the response for a request to the fake host: one of the embedded Mermaid files, or 404.</summary>
    private static CoreWebView2WebResourceResponse CreateEmbeddedFileResponse(CoreWebView2Environment browserEnvironment, string requestAddress)
    {
        // Map the requested file name to the resource name given in MarkdownViewer.csproj.
        string requestedFileName = new Uri(requestAddress).AbsolutePath.TrimStart('/');
        (string resourceName, string contentType)? embeddedFile = requestedFileName switch
        {
            "MermaidHost.html" => ("MarkdownViewer.Mermaid.MermaidHost.html", "text/html; charset=utf-8"),
            "mermaid.min.js" => ("MarkdownViewer.Mermaid.mermaid.min.js", "text/javascript; charset=utf-8"),
            _ => null,
        };

        var resourceStream = embeddedFile == null
            ? null
            : Assembly.GetExecutingAssembly().GetManifestResourceStream(embeddedFile.Value.resourceName);
        if (resourceStream == null)
        {
            return browserEnvironment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }

        return browserEnvironment.CreateWebResourceResponse(resourceStream, 200, "OK", $"Content-Type: {embeddedFile!.Value.contentType}");
    }

    /// <summary>Handles messages posted by the host page: the "ready" signal and finished (or failed) diagrams.</summary>
    private void HandleWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs messageDetails)
    {
        try
        {
            using var parsedMessage = JsonDocument.Parse(messageDetails.TryGetWebMessageAsString());
            var messageRoot = parsedMessage.RootElement;

            if (messageRoot.TryGetProperty("ready", out _))
            {
                pageReadySignal.TrySetResult();
                return;
            }

            int requestId = messageRoot.GetProperty("requestId").GetInt32();
            if (!pendingRequestsById.TryGetValue(requestId, out var answerSource))
            {
                return; // The request already timed out.
            }

            if (messageRoot.TryGetProperty("error", out var errorProperty))
            {
                answerSource.TrySetResult(MermaidRenderOutcome.Failed(errorProperty.GetString() ?? "Unknown Mermaid error."));
                return;
            }

            var diagramBitmap = DecodePngDataUrl(messageRoot.GetProperty("pngDataUrl").GetString() ?? string.Empty);
            var diagramImage = new MermaidDiagramImage(
                diagramBitmap,
                messageRoot.GetProperty("width").GetDouble(),
                messageRoot.GetProperty("height").GetDouble());
            answerSource.TrySetResult(MermaidRenderOutcome.Succeeded(diagramImage));
        }
        catch (Exception messageError)
        {
            // A malformed message should never crash the app. Fail every waiting diagram so they show their code.
            foreach (var waitingAnswer in pendingRequestsById.Values)
            {
                waitingAnswer.TrySetResult(MermaidRenderOutcome.Failed(messageError.Message));
            }
        }
    }

    /// <summary>Converts a "data:image/png;base64,..." URL from the page into a frozen WPF bitmap.</summary>
    private static BitmapSource DecodePngDataUrl(string pngDataUrl)
    {
        int base64Start = pngDataUrl.IndexOf(',') + 1;
        byte[] pngBytes = Convert.FromBase64String(pngDataUrl[base64Start..]);

        var diagramBitmap = new BitmapImage();
        using (var pngStream = new MemoryStream(pngBytes))
        {
            diagramBitmap.BeginInit();
            diagramBitmap.CacheOption = BitmapCacheOption.OnLoad; // read everything now so the stream can be closed
            diagramBitmap.StreamSource = pngStream;
            diagramBitmap.EndInit();
        }
        // Frozen bitmaps are immutable and can be shared between the screen and print documents.
        diagramBitmap.Freeze();
        return diagramBitmap;
    }
}

/// <summary>Result of rendering one diagram: either a picture or the reason it could not be drawn.</summary>
/// <param name="DiagramImage">The picture, or null when rendering failed.</param>
/// <param name="ErrorMessage">Why rendering failed, or null on success.</param>
public sealed record MermaidRenderOutcome(MermaidDiagramImage? DiagramImage, string? ErrorMessage)
{
    /// <summary>Creates a successful outcome.</summary>
    public static MermaidRenderOutcome Succeeded(MermaidDiagramImage diagramImage) => new(diagramImage, null);

    /// <summary>Creates a failed outcome with a short, user-readable reason.</summary>
    public static MermaidRenderOutcome Failed(string errorMessage) => new(null, errorMessage);
}
