namespace MeshAI.Network.Transport;

/// <summary>
/// Explicit connection state machine states.
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// Connection not established.
    /// </summary>
    Disconnected,

    /// <summary>
    /// TCP connection being established.
    /// </summary>
    Connecting,

    /// <summary>
    /// TCP connected, key exchange in progress.
    /// </summary>
    KeyExchange,

    /// <summary>
    /// Fully connected and authenticated.
    /// </summary>
    Connected,

    /// <summary>
    /// Connection is being gracefully closed.
    /// </summary>
    Disconnecting,

    /// <summary>
    /// Connection failed with error.
    /// </summary>
    Failed
}

/// <summary>
/// Connection state transition events.
/// </summary>
public enum ConnectionEvent
{
    /// <summary>
    /// Start connection attempt.
    /// </summary>
    Connect,

    /// <summary>
    /// TCP connection established.
    /// </summary>
    TcpConnected,

    /// <summary>
    /// Key exchange completed successfully.
    /// </summary>
    KeyExchangeComplete,

    /// <summary>
    /// Initiate disconnect.
    /// </summary>
    Disconnect,

    /// <summary>
    /// Connection closed.
    /// </summary>
    Closed,

    /// <summary>
    /// Error occurred.
    /// </summary>
    Error,

    /// <summary>
    /// Timeout occurred.
    /// </summary>
    Timeout,

    /// <summary>
    /// Heartbeat timeout (peer unresponsive).
    /// </summary>
    HeartbeatTimeout
}

/// <summary>
/// Connection state machine with explicit transitions.
/// </summary>
public sealed class ConnectionStateMachine
{
    private ConnectionState _currentState = ConnectionState.Disconnected;
    private readonly object _lock = new();

    /// <summary>
    /// Current connection state.
    /// </summary>
    public ConnectionState CurrentState
    {
        get { lock (_lock) return _currentState; }
    }

    /// <summary>
    /// Event raised when state changes.
    /// </summary>
    public event Action<ConnectionState, ConnectionState>? StateChanged;

    /// <summary>
    /// Attempts to transition to a new state based on an event.
    /// Returns true if transition was valid and performed.
    /// </summary>
    public bool TryTransition(ConnectionEvent evt, out ConnectionState newState)
    {
        lock (_lock)
        {
            var oldState = _currentState;
            newState = GetNextState(_currentState, evt);

            if (newState == _currentState && evt != ConnectionEvent.Error)
            {
                // No transition (invalid event for current state)
                return false;
            }

            _currentState = newState;

            if (oldState != newState)
            {
                StateChanged?.Invoke(oldState, newState);
            }

            return true;
        }
    }

    /// <summary>
    /// Gets the next state for a given current state and event.
    /// </summary>
    private static ConnectionState GetNextState(ConnectionState current, ConnectionEvent evt)
    {
        return (current, evt) switch
        {
            // From Disconnected
            (ConnectionState.Disconnected, ConnectionEvent.Connect) => ConnectionState.Connecting,

            // From Connecting
            (ConnectionState.Connecting, ConnectionEvent.TcpConnected) => ConnectionState.KeyExchange,
            (ConnectionState.Connecting, ConnectionEvent.Error) => ConnectionState.Failed,
            (ConnectionState.Connecting, ConnectionEvent.Timeout) => ConnectionState.Failed,

            // From KeyExchange
            (ConnectionState.KeyExchange, ConnectionEvent.KeyExchangeComplete) => ConnectionState.Connected,
            (ConnectionState.KeyExchange, ConnectionEvent.Error) => ConnectionState.Failed,
            (ConnectionState.KeyExchange, ConnectionEvent.Timeout) => ConnectionState.Failed,

            // From Connected
            (ConnectionState.Connected, ConnectionEvent.Disconnect) => ConnectionState.Disconnecting,
            (ConnectionState.Connected, ConnectionEvent.Error) => ConnectionState.Failed,
            (ConnectionState.Connected, ConnectionEvent.Closed) => ConnectionState.Disconnected,
            (ConnectionState.Connected, ConnectionEvent.HeartbeatTimeout) => ConnectionState.Failed,

            // From Disconnecting
            (ConnectionState.Disconnecting, ConnectionEvent.Closed) => ConnectionState.Disconnected,
            (ConnectionState.Disconnecting, ConnectionEvent.Timeout) => ConnectionState.Disconnected,

            // From Failed
            (ConnectionState.Failed, ConnectionEvent.Closed) => ConnectionState.Disconnected,
            (ConnectionState.Failed, ConnectionEvent.Connect) => ConnectionState.Connecting,

            // Default: stay in current state
            _ => current
        };
    }

    /// <summary>
    /// Checks if a transition would be valid without performing it.
    /// </summary>
    public bool CanTransition(ConnectionEvent evt)
    {
        lock (_lock)
        {
            var next = GetNextState(_currentState, evt);
            return next != _currentState || evt == ConnectionEvent.Error;
        }
    }

    /// <summary>
    /// Forces the state machine to a specific state (for recovery).
    /// </summary>
    public void ForceState(ConnectionState state)
    {
        lock (_lock)
        {
            var old = _currentState;
            _currentState = state;
            if (old != state)
            {
                StateChanged?.Invoke(old, state);
            }
        }
    }

    /// <summary>
    /// Returns whether the connection is in a connected state.
    /// </summary>
    public bool IsConnected => CurrentState == ConnectionState.Connected;

    /// <summary>
    /// Returns whether the connection is in a failed state.
    /// </summary>
    public bool IsFailed => CurrentState == ConnectionState.Failed;

    /// <summary>
    /// Returns whether the connection is in transition.
    /// </summary>
    public bool IsTransitioning => CurrentState is ConnectionState.Connecting
                                                  or ConnectionState.KeyExchange
                                                  or ConnectionState.Disconnecting;
}
