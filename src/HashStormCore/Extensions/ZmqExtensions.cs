using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using HashStormCore.Mining;
using NLog;
using ZeroMQ;
using ZeroMQ.Monitoring;

namespace HashStormCore.Extensions;

public static class ZmqExtensions
{
    private const string ZapEndpoint = "inproc://zeromq.zap.01";
    private const string ZapDomain = "hashstorm-relay";
    private const int Z85KeyLength = 40;
    private const int CurveKeyLength = 32;

    private static long monitorSocketIndex = 0;

    public static IObservable<ZMonitorEventArgs> MonitorAsObservable(this ZSocket socket)
    {
        return Observable.Defer(() => Observable.Create<ZMonitorEventArgs>(obs =>
        {
            var url = $"inproc://monitor{Interlocked.Increment(ref monitorSocketIndex)}";
            var monitor = ZMonitor.Create(socket.Context, url);
            var cts = new CancellationTokenSource();

            void OnEvent(object sender, ZMonitorEventArgs e)
            {
                obs.OnNext(e);
            }

            monitor.AllEvents += OnEvent;

            socket.Monitor(url);
            monitor.Start(cts);

            return Disposable.Create(() =>
            {
                using(new CompositeDisposable(monitor, cts))
                {
                    monitor.AllEvents -= OnEvent;
                    monitor.Stop();
                }
            });
        }));
    }

    public static void LogMonitorEvent(ILogger logger, ZMonitorEventArgs e)
    {
        logger.Info(() => $"[ZMQ] [{e.Event.Address}] {Enum.GetName(typeof(ZMonitorEvents), e.Event.Event)} [{e.Event.EventValue}]");
    }

    /// <summary>
    /// Sets up server-side socket to utilize ZeroMQ Curve Transport-Layer Security
    /// </summary>
    public static IDisposable SetupCurveTlsServer(this ZSocket socket, string curveServerSecretKey, string[] allowedClientPublicKeys, ILogger logger)
    {
        curveServerSecretKey = curveServerSecretKey?.Trim();

        if(string.IsNullOrEmpty(curveServerSecretKey))
        {
            if(allowedClientPublicKeys?.Any(x => !string.IsNullOrEmpty(x?.Trim())) == true)
                throw new PoolStartupException("ZeroMQ Curve server secret key is required when allowed client public keys are configured");

            return Disposable.Empty;
        }

        if(allowedClientPublicKeys == null || allowedClientPublicKeys.All(x => string.IsNullOrEmpty(x?.Trim())))
            throw new PoolStartupException("ZeroMQ Curve allowed client public keys are required when a server secret key is configured");

        if(!ZContext.Has("curve"))
            throw new PoolStartupException("Unable to initialize ZMQ Curve Transport-Layer-Security. Your ZMQ library was compiled without Curve support!");

        var serverSecretKey = DecodeZ85Key(curveServerSecretKey, "ZeroMQ Curve server secret key");
        var serverPubKey = DerivePublicKey(curveServerSecretKey, "ZeroMQ Curve server secret key");
        var authenticator = new CurveZapAuthenticator(socket.Context, allowedClientPublicKeys, logger);

        try
        {
            authenticator.Start();
        }

        catch
        {
            authenticator.Dispose();
            throw;
        }

        socket.CurveServer = true;
        socket.ZAPDomain = ZapDomain;
        socket.CurveSecretKey = serverSecretKey;
        socket.CurvePublicKey = serverPubKey;

        return authenticator;
    }

    /// <summary>
    /// Sets up client-side socket to utilize ZeroMQ Curve Transport-Layer Security
    /// </summary>
    public static void SetupCurveTlsClient(this ZSocket socket, string curveServerPublicKey, string curveClientSecretKey, ILogger logger)
    {
        curveServerPublicKey = curveServerPublicKey?.Trim();
        curveClientSecretKey = curveClientSecretKey?.Trim();

        if(string.IsNullOrEmpty(curveServerPublicKey) && string.IsNullOrEmpty(curveClientSecretKey))
            return;

        if(string.IsNullOrEmpty(curveServerPublicKey) || string.IsNullOrEmpty(curveClientSecretKey))
            throw new PoolStartupException("ZeroMQ Curve server public key and client secret key must be configured together");

        if(!ZContext.Has("curve"))
            throw new PoolStartupException("Unable to initialize ZMQ Curve Transport-Layer-Security. Your ZMQ library was compiled without Curve support!");

        var serverPubKey = DecodeZ85Key(curveServerPublicKey, "ZeroMQ Curve server public key");
        var clientSecretKey = DecodeZ85Key(curveClientSecretKey, "ZeroMQ Curve client secret key");
        var clientPubKey = DerivePublicKey(curveClientSecretKey, "ZeroMQ Curve client secret key");

        socket.CurveServer = false;
        socket.CurveServerKey = serverPubKey;
        socket.CurveSecretKey = clientSecretKey;
        socket.CurvePublicKey = clientPubKey;
    }

    private static byte[] DecodeZ85Key(string key, string keyName)
    {
        if(string.IsNullOrWhiteSpace(key))
            throw new PoolStartupException($"{keyName} is required");

        key = key.Trim();

        if(key.Length != Z85KeyLength)
            throw new PoolStartupException($"{keyName} must be a 40-character ZeroMQ Curve Z85 key");

        for(var i = 0; i < key.Length; i++)
        {
            if(key[i] > 127)
                throw new PoolStartupException($"{keyName} must contain only ASCII Z85 characters");
        }

        try
        {
            var decoded = key.ToZ85DecodedBytes(Encoding.ASCII);

            if(decoded.Length != CurveKeyLength)
                throw new PoolStartupException($"{keyName} must decode to a 32-byte ZeroMQ Curve key");

            return decoded;
        }

        catch(PoolStartupException)
        {
            throw;
        }

        catch(Exception ex)
        {
            throw new PoolStartupException($"{keyName} is not a valid ZeroMQ Curve Z85 key: {ex.Message}");
        }
    }

    private static byte[] DerivePublicKey(string secretKey, string keyName)
    {
        try
        {
            Z85.CurvePublic(out var publicKey, Encoding.ASCII.GetBytes(secretKey.Trim()));
            return DecodeZ85Key(Encoding.ASCII.GetString(publicKey), $"{keyName} public key");
        }

        catch(PoolStartupException)
        {
            throw;
        }

        catch(Exception ex)
        {
            throw new PoolStartupException($"Unable to derive ZeroMQ Curve public key from {keyName}: {ex.Message}");
        }
    }

    private sealed class CurveZapAuthenticator : IDisposable
    {
        public CurveZapAuthenticator(ZContext context, IEnumerable<string> allowedClientPublicKeys, ILogger logger)
        {
            this.context = context;
            this.logger = logger;
            this.allowedClientPublicKeys = allowedClientPublicKeys
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Convert.ToBase64String(DecodeZ85Key(x, "ZeroMQ Curve allowed client public key")))
                .ToHashSet(StringComparer.Ordinal);
        }

        private readonly ZContext context;
        private readonly ILogger logger;
        private readonly HashSet<string> allowedClientPublicKeys;
        private readonly CancellationTokenSource cts = new();
        private readonly ManualResetEventSlim started = new();
        private Exception startupException;
        private Task task;

        public void Start()
        {
            task = Task.Run(Run);

            if(!started.Wait(TimeSpan.FromSeconds(5)))
                throw new PoolStartupException("Timed out while starting ZeroMQ ZAP authenticator");

            if(startupException != null)
                throw new PoolStartupException($"Unable to start ZeroMQ ZAP authenticator: {startupException.Message}");
        }

        private void Run()
        {
            try
            {
                using var socket = new ZSocket(context, ZSocketType.REP);

                socket.Bind(ZapEndpoint);
                started.Set();

                var sockets = new[] { socket };
                var pollItems = new[] { ZPollItem.CreateReceiver() };
                var timeout = TimeSpan.FromMilliseconds(250);

                while(!cts.IsCancellationRequested)
                {
                    try
                    {
                        if(!sockets.PollIn(pollItems, out var messages, out var error, timeout))
                            continue;

                        if(error != null)
                        {
                            logger.Error(() => $"ZeroMQ ZAP authenticator: {error.Name} [{error.Name}] during receive");
                            continue;
                        }

                        using var request = messages[0];

                        if(request == null)
                            continue;

                        using var response = CreateResponse(request);
                        socket.SendMessage(response);
                    }

                    catch(ObjectDisposedException)
                    {
                        break;
                    }

                    catch(Exception ex)
                    {
                        if(!cts.IsCancellationRequested)
                            logger.Error(ex, "ZeroMQ ZAP authentication error");
                    }
                }
            }

            catch(Exception ex)
            {
                startupException = ex;
                started.Set();

                if(!cts.IsCancellationRequested)
                    logger.Error(ex, "ZeroMQ ZAP authenticator stopped");
            }
        }

        private ZMessage CreateResponse(ZMessage request)
        {
            var version = request.Count > 0 ? request[0].ReadString(Encoding.ASCII) : "1.0";
            var sequence = request.Count > 1 ? request[1].ReadString(Encoding.ASCII) : string.Empty;
            var domain = request.Count > 2 ? request[2].ReadString(Encoding.ASCII) : string.Empty;
            var mechanism = request.Count > 5 ? request[5].ReadString(Encoding.ASCII) : string.Empty;
            var isAuthorized = false;

            if(version == "1.0" && domain == ZapDomain && mechanism == "CURVE" && request.Count > 6)
            {
                var clientPublicKey = request[6].Read();
                isAuthorized = clientPublicKey.Length == CurveKeyLength &&
                    allowedClientPublicKeys.Contains(Convert.ToBase64String(clientPublicKey));
            }

            var response = new ZMessage();
            response.Add(new ZFrame(version));
            response.Add(new ZFrame(sequence));

            if(isAuthorized)
            {
                response.Add(new ZFrame("200"));
                response.Add(new ZFrame("OK"));
                response.Add(new ZFrame("relay-client"));
            }

            else
            {
                response.Add(new ZFrame("400"));
                response.Add(new ZFrame("Client key not allowed"));
                response.Add(new ZFrame(string.Empty));
            }

            response.Add(new ZFrame(string.Empty));
            return response;
        }

        public void Dispose()
        {
            cts.Cancel();

            try
            {
                task?.Wait(TimeSpan.FromSeconds(2));
            }

            catch(AggregateException)
            {
            }

            cts.Dispose();
            started.Dispose();
        }
    }
}
