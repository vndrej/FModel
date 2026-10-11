using System;
using System.Net;
using System.Threading.Tasks;
using FModel.Settings;
using FModel.Views.Resources.Controls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;

namespace FModel.Mcp;

/// <summary>
/// Hosts the Model Context Protocol server inside the running FModel process.
/// Transport is Streamable HTTP on the loopback interface only; AI clients
/// (Claude Code, Claude Desktop, Codex, ChatGPT, ...) connect to http://127.0.0.1:{port}/mcp.
/// </summary>
public static class McpServerHost
{
    private static WebApplication _app;
    private static int _runningPort;
    private static readonly SemaphoreSlimLock _lock = new();

    public static bool IsRunning => _app != null;
    public static string Endpoint => _app is null ? null : $"http://127.0.0.1:{_runningPort}/mcp";

    public static async Task StartAsync()
    {
        using (await _lock.EnterAsync())
        {
            if (_app != null || !UserSettings.Default.McpEnabled)
                return;

            var port = UserSettings.Default.McpPort;
            if (port is < 1 or > 65535)
            {
                FLogger.Append(ELog.Error, () => FLogger.Text($"MCP server not started: invalid port {port}", Constants.WHITE, true));
                return;
            }

            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    ContentRootPath = AppContext.BaseDirectory,
                    ApplicationName = "FModel"
                });

                builder.Logging.ClearProviders();
                builder.Logging.AddProvider(new SerilogForwardingLoggerProvider());
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
                builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));

                // FModel owns the process lifetime. The default ConsoleLifetime hooks Ctrl+C and ProcessExit and
                // blocks process exit until the host is disposed, which must not happen inside a WPF app.
                builder.Services.AddSingleton<IHostLifetime, PassiveHostLifetime>();

                builder.Services
                    .AddMcpServer(options =>
                    {
                        options.ServerInfo = new Implementation
                        {
                            Name = "FModel",
                            Title = "FModel",
                            Version = Constants.APP_VERSION
                        };
                        options.ServerInstructions = FModelMcpTools.Instructions;
                    })
                    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
                    .WithTools<FModelMcpTools>();

                var app = builder.Build();

                // Only loopback host names are accepted, protecting against DNS rebinding from a browser.
                app.Use(async (context, next) =>
                {
                    if (!IsLoopbackHost(context.Request.Host.Host))
                    {
                        context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                        return;
                    }

                    await next(context);
                });

                app.MapGet("/", () => Results.Text($"FModel {Constants.APP_VERSION} MCP server. Streamable HTTP endpoint: /mcp"));
                app.MapMcp("/mcp");

                await app.StartAsync().ConfigureAwait(false);
                _app = app;
                _runningPort = port;

                Log.Information("MCP server listening on {Endpoint}", Endpoint);
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("MCP server listening on ", Constants.WHITE);
                    FLogger.Link(Endpoint, Endpoint, true);
                });
            }
            catch (Exception e)
            {
                Log.Error(e, "MCP server failed to start on port {Port}", port);
                var reason = e.GetBaseException() is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
                    ? $"port {port} is already in use (another FModel instance?). Change the port in Settings > General."
                    : e.GetBaseException().Message;
                FLogger.Append(ELog.Error, () => FLogger.Text($"MCP server failed to start: {reason}", Constants.WHITE, true));
            }
        }
    }

    /// <summary>
    /// Safe to call from the UI thread: nothing in here resumes on the caller's synchronization context.
    /// </summary>
    public static async Task StopAsync()
    {
        using (await _lock.EnterAsync().ConfigureAwait(false))
        {
            if (_app is null) return;

            try
            {
                using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(timeout.Token).ConfigureAwait(false);
                await _app.DisposeAsync().ConfigureAwait(false);
                Log.Information("MCP server stopped");
            }
            catch (Exception e)
            {
                Log.Warning(e, "MCP server did not stop cleanly");
            }
            finally
            {
                _app = null;
            }
        }
    }

    /// <summary>
    /// Called after the settings window is saved: start, stop or move the server to match the new settings.
    /// </summary>
    public static async Task ApplySettingsAsync()
    {
        var wantRunning = UserSettings.Default.McpEnabled;
        var portChanged = IsRunning && _runningPort != UserSettings.Default.McpPort;

        if (IsRunning && (!wantRunning || portChanged))
            await StopAsync();
        if (wantRunning && !IsRunning)
            await StartAsync();
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Forwards warnings and errors from the ASP.NET Core host to FModel's Serilog log file.
    /// </summary>
    private sealed class SerilogForwardingLoggerProvider : ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new ForwardingLogger(categoryName);
        public void Dispose() { }

        private sealed class ForwardingLogger(string category) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var message = formatter(state, exception);
                if (logLevel >= LogLevel.Error)
                    Serilog.Log.Error(exception, "[MCP:{Category}] {Message}", category, message);
                else
                    Serilog.Log.Warning(exception, "[MCP:{Category}] {Message}", category, message);
            }
        }
    }

    /// <summary>Does not hook Ctrl+C or ProcessExit; start and stop are driven explicitly by this class.</summary>
    private sealed class PassiveHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SemaphoreSlimLock
    {
        private readonly System.Threading.SemaphoreSlim _semaphore = new(1, 1);

        public async Task<IDisposable> EnterAsync()
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            return new Releaser(_semaphore);
        }

        private sealed class Releaser(System.Threading.SemaphoreSlim semaphore) : IDisposable
        {
            public void Dispose() => semaphore.Release();
        }
    }
}
