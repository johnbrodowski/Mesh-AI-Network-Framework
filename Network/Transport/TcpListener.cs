using System.Net;
using System.Net.Sockets;

namespace MeshAI.Network.Transport;

/// <summary>
/// TCP listener for accepting incoming connections.
/// </summary>
public sealed class ConnectionListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private bool _isListening;

    /// <summary>
    /// Local endpoint the listener is bound to.
    /// </summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// Whether the listener is currently accepting connections.
    /// </summary>
    public bool IsListening => _isListening;

    /// <summary>
    /// Event raised when a new connection is accepted.
    /// </summary>
    public event Action<TcpConnection>? ConnectionAccepted;

    /// <summary>
    /// Event raised when an error occurs while accepting.
    /// </summary>
    public event Action<Exception>? AcceptError;

    /// <summary>
    /// Creates a new listener on the specified endpoint.
    /// </summary>
    public ConnectionListener(IPEndPoint endpoint)
    {
        LocalEndPoint = endpoint;
        _listener = new TcpListener(endpoint);
    }

    /// <summary>
    /// Creates a new listener on any address with the specified port.
    /// </summary>
    public ConnectionListener(int port)
        : this(new IPEndPoint(IPAddress.Any, port))
    {
    }

    /// <summary>
    /// Starts listening for incoming connections.
    /// </summary>
    public void Start()
    {
        if (_isListening)
            throw new InvalidOperationException("Already listening");

        _listener.Start();
        _isListening = true;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);

                // Configure client
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;

                var connection = new TcpConnection(client);
                connection.Initialize();

                ConnectionAccepted?.Invoke(connection);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AcceptError?.Invoke(ex);
            }
        }
    }

    /// <summary>
    /// Stops listening for connections.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isListening)
            return;

        _isListening = false;
        await _cts.CancelAsync();
        _listener.Stop();

        if (_acceptLoop != null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // Ignore
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
