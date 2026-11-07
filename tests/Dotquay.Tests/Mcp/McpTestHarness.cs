using System.IO.Pipelines;
using Dotquay.Mcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dotquay.Tests.Mcp;

/// <summary>
/// An in-process dotquay MCP server plus a real <see cref="McpClient"/> connected to it over an
/// in-memory duplex pipe pair.
/// </summary>
/// <remarks>
/// This is design doc §8 test layer 1, "handler conformance": <see cref="McpServerBuilder.BuildForStreams"/>
/// is the exact same tool/resource/prompt wiring <c>dotquay mcp</c> uses over real stdio, so a test
/// built on this harness exercises production's handlers end to end — the initialize handshake, tool/
/// resource/prompt dispatch, capability negotiation, JSON envelope serialization — without
/// spawning a process or touching a real terminal. Also captures whatever the process under test
/// writes to <see cref="Console.Out"/> (see <see cref="CapturedStdout"/>), separately from the pipe the
/// transport itself uses, so a stdout-purity test can assert nothing ever landed there.
/// </remarks>
internal sealed class McpTestHarness : IAsyncDisposable {
    private readonly IHost _host;
    private readonly Task _hostTask;
    private readonly CancellationTokenSource _cancellation;
    private readonly StringWriter _capturedStdout;
    private readonly TextWriter _originalStdout;

    private McpTestHarness(
        IHost host,
        Task hostTask,
        CancellationTokenSource cancellation,
        McpClient client,
        StringWriter capturedStdout,
        TextWriter originalStdout) {
        _host = host;
        _hostTask = hostTask;
        _cancellation = cancellation;
        Client = client;
        _capturedStdout = capturedStdout;
        _originalStdout = originalStdout;
    }

    public McpClient Client { get; }

    /// <summary>Everything written to <see cref="Console.Out"/> since <see cref="StartAsync"/> — real
    /// stdout, never the in-memory pipe the transport itself reads and writes. Must always be empty;
    /// see <see cref="McpServerBuilder"/>'s remarks on why a non-empty result here would mean the
    /// MCP wire is corrupted under real stdio.</summary>
    public string CapturedStdout => _capturedStdout.ToString();

    /// <param name="clientOptions">Lets a test configure the simulated client's own capabilities and
    /// handlers, or pin its protocol version. Null (the default) matches every pre-Phase-3 test: a client that advertises
    /// nothing beyond the SDK's own defaults.</param>
    /// <param name="logFile">Forwarded to <see cref="McpServerBuilder.BuildForStreams"/> so a test can
    /// exercise <c>--log</c> (design doc §6) against the same in-process server.</param>
    public static async Task<McpTestHarness> StartAsync(
        IReadOnlyList<string> projectRoots, McpClientOptions? clientOptions = null, string? logFile = null) {
        var originalStdout = Console.Out;
        var capturedStdout = new StringWriter();
        Console.SetOut(capturedStdout);

        var serverToClient = new Pipe();
        var clientToServer = new Pipe();
        var cancellation = new CancellationTokenSource();

        try {
            var host = McpServerBuilder.BuildForStreams(
                projectRoots,
                input: clientToServer.Reader.AsStream(),
                output: serverToClient.Writer.AsStream(),
                logFile);

            var hostTask = host.RunAsync(cancellation.Token);

            var client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    serverInput: clientToServer.Writer.AsStream(),
                    serverOutput: serverToClient.Reader.AsStream(),
                    loggerFactory: NullLoggerFactory.Instance),
                clientOptions,
                null,
                cancellation.Token);

            return new McpTestHarness(host, hostTask, cancellation, client, capturedStdout, originalStdout);
        } catch {
            Console.SetOut(originalStdout);
            throw;
        }
    }

    public async ValueTask DisposeAsync() {
        try {
            await Client.DisposeAsync();
            await _cancellation.CancelAsync();

            try {
                await _hostTask;
            } catch (OperationCanceledException) {
                // Expected: cancelling _cancellation is how a stream-transport host is asked to stop.
            }
        } finally {
            Console.SetOut(_originalStdout);
            _host.Dispose();
            _cancellation.Dispose();
        }
    }
}
