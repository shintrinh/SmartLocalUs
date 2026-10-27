// Proves the MetaResponder tool loop without any real API or Twilio:
// a tiny in-process HTTP stub stands in for api.meta.ai, so the test is
// deterministic. The stub serves a canned tool_calls response first and a
// final text response second; the test asserts the responder made exactly
// those two requests, sent the tool definition, and returned the final text
// (not the tool call, not a fallback). A second test proves the stage
// guidance reaches the request as a system message.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AirwayHVAC.SmsAi.Domain.Entities;
using AirwayHVAC.SmsAi.Domain.Enums;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using AirwayHVAC.SmsAi.Infrastructure.Configuration;
using AirwayHVAC.SmsAi.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace AirwayHVAC.SmsAi.Application.Tests.Services;

public sealed class MetaResponderToolLoopTests
{
    private static readonly Tenant Tenant = new(
        "tenant_123",
        "tenant_123",
        "CoolAir HVAC",
        "+18777804236",
        "Houston TX",
        "America/Chicago",
        new BusinessHours("08:00", "18:00"),
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task ToolCall_RoundTrips_AndReturnsFinalText()
    {
        var toolResponse = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call_1",
                                type = "function",
                                function = new
                                {
                                    name = "book_appointment",
                                    arguments = "{\"caller_name\":\"Maria\",\"day\":\"Saturday\",\"service\":\"haircut\"}"
                                }
                            }
                        }
                    }
                }
            }
        });
        var finalResponse = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = "Got it, Maria — haircut on Saturday is booked."
                    }
                }
            }
        });

        await using var server = new StubApiServer([toolResponse, finalResponse]);
        server.Start();
        var responder = CreateResponder(server.Port);

        var reply = await responder.GenerateHvacResponseAsync(
            Tenant, "+15550001111", [], "Book me a haircut on Saturday", CancellationToken.None);

        await server.WaitAsync();
        Assert.Equal("Got it, Maria — haircut on Saturday is booked.", reply);
        Assert.Equal(2, server.Bodies.Count);
        Assert.Contains("book_appointment", server.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"tools\"", server.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"tool\"", server.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("call_1", server.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageContext_SentAsSystemMessage()
    {
        var plainResponse = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = "What is the service address?"
                    }
                }
            }
        });

        await using var server = new StubApiServer([plainResponse]);
        server.Start();
        var responder = CreateResponder(server.Port);
        var context = new AiReplyContext(
            ConversationStage.Address, "AC not cooling", null, null, null);

        var reply = await responder.GenerateHvacResponseAsync(
            Tenant, "+15550001111", [], "Hello", CancellationToken.None, context);

        await server.WaitAsync();
        Assert.Equal("What is the service address?", reply);
        Assert.Single(server.Bodies);
        Assert.Contains("Stage guidance", server.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("Address", server.Bodies[0], StringComparison.Ordinal);
    }

    private static MetaResponder CreateResponder(int port)
        => new(new StaticOptions<MetaOptions>(new MetaOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            ApiKey = "test-key",
            Model = "test-model"
        }));

    private sealed class StaticOptions<T> : IOptions<T>
        where T : class
    {
        public StaticOptions(T value)
        {
            Value = value;
        }

        public T Value { get; }
    }

    // Minimal HTTP/1.1 stub: accepts a fixed number of POSTs, records their
    // bodies, and answers each with a canned JSON payload. No HttpListener
    // (avoids URL-ACL quirks); plain TcpListener works on every OS.
    private sealed class StubApiServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string[] _responses;
        private readonly List<string> _bodies = [];
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));
        private Task? _serveTask;

        public StubApiServer(string[] responses)
        {
            _responses = responses;
            _listener = new TcpListener(IPAddress.Loopback, 0);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyList<string> Bodies => _bodies;

        public void Start()
        {
            _listener.Start();
            _serveTask = ServeAsync();
        }

        public Task WaitAsync() => _serveTask ?? Task.CompletedTask;

        private async Task ServeAsync()
        {
            for (var i = 0; i < _responses.Length; i++)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                using var stream = client.GetStream();
                var body = await ReadRequestBodyAsync(stream, _cts.Token);
                lock (_bodies)
                {
                    _bodies.Add(body);
                }

                var payload = Encoding.UTF8.GetBytes(_responses[i]);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, _cts.Token);
                await stream.WriteAsync(payload, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
        }

        private static async Task<string> ReadRequestBodyAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[4096];
            using var raw = new MemoryStream();
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }

                raw.Write(buffer, 0, read);
                var text = Encoding.ASCII.GetString(raw.GetBuffer(), 0, (int)raw.Length);
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }

            if (headerEnd < 0)
            {
                return string.Empty;
            }

            var headerText = Encoding.ASCII.GetString(raw.GetBuffer(), 0, (int)raw.Length);
            if (headerText.Contains("Expect: 100-continue", StringComparison.OrdinalIgnoreCase))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), ct);
            }

            var contentLength = 0;
            foreach (var line in headerText[..headerEnd].Split(["\r\n"], StringSplitOptions.None))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["Content-Length:".Length..].Trim(), out var n))
                {
                    contentLength = n;
                }
            }

            var bodyStart = headerEnd + 4;
            var have = (int)raw.Length - bodyStart;
            while (have < contentLength)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }

                raw.Write(buffer, 0, read);
                have += read;
            }

            var bytes = raw.ToArray();
            var available = Math.Min(contentLength, bytes.Length - bodyStart);
            return available <= 0 ? string.Empty : Encoding.UTF8.GetString(bytes, bodyStart, available);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            if (_serveTask is not null)
            {
                try
                {
                    await _serveTask;
                }
                catch (Exception)
                {
                    // Server task is best-effort teardown; test assertions own the verdict.
                }
            }

            _cts.Dispose();
        }
    }
}
