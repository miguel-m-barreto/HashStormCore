using System.Reflection;
using Autofac;
using HashStormCore.Api;
using HashStormCore.Banning;
using HashStormCore.Blockchain.Alephium;
using HashStormCore.Blockchain.Beam;
using HashStormCore.Blockchain.Bitcoin;
using HashStormCore.Blockchain.Conceal;
using HashStormCore.Blockchain.Cryptonote;
using HashStormCore.Blockchain.Equihash;
using HashStormCore.Blockchain.Ergo;
using HashStormCore.Blockchain.Ethereum;
using HashStormCore.Blockchain.Handshake;
using HashStormCore.Blockchain.Kaspa;
using HashStormCore.Blockchain.Nexa;
using HashStormCore.Blockchain.Progpow;
using HashStormCore.Blockchain.Satoshicash;
using HashStormCore.Blockchain.Warthog;
using HashStormCore.Blockchain.Xelis;
using HashStormCore.Blockchain.Zano;
using HashStormCore.Configuration;
using HashStormCore.Crypto;
using HashStormCore.Crypto.Hashing.Equihash;
using HashStormCore.Crypto.Hashing.Ethash;
using HashStormCore.Crypto.Hashing.Progpow;
using HashStormCore.Eventing.Abstractions;
using HashStormCore.Eventing.Configuration;
using HashStormCore.Eventing.Outbox;
using HashStormCore.Eventing.Publishing;
using HashStormCore.Eventing.Queue;
using HashStormCore.Eventing.Transport;
using HashStormCore.Messaging;
using HashStormCore.Mining;
using HashStormCore.Notifications;
using HashStormCore.Payments;
using HashStormCore.Payments.PaymentSchemes;
using HashStormCore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Module = Autofac.Module;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IO;
using HashStormCore.Nicehash;
using HashStormCore.Pushover;

namespace HashStormCore;

public class AutofacModule : Module
{
    /// <summary>
    /// Override to add registrations to the container.
    /// </summary>
    /// <remarks>
    /// Note that the ContainerBuilder parameter is unique to this module.
    /// </remarks>
    /// <param name="builder">The builder through which components can be registered.</param>
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterInstance(new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy
                {
                    ProcessDictionaryKeys = false
                }
            }
        });

        builder.RegisterType<MessageBus>()
            .AsImplementedInterfaces()
            .SingleInstance();

        builder.Register(ctx =>
        {
            var clusterConfig = ctx.ResolveOptional<ClusterConfig>();

            return new RecyclableMemoryStreamManager(
                new RecyclableMemoryStreamManager.Options
                {
                    ThrowExceptionOnToArray = true,
                    MaximumSmallPoolFreeBytes = clusterConfig?.Memory?.RmsmMaximumFreeSmallPoolBytes ?? 0x100000,
                    MaximumLargePoolFreeBytes = clusterConfig?.Memory?.RmsmMaximumFreeLargePoolBytes ?? 0x800000
                });
        }).SingleInstance();

        builder.RegisterType<StandardClock>()
            .AsImplementedInterfaces()
            .SingleInstance();

        builder.RegisterType<IntegratedBanManager>()
            .Keyed<IBanManager>(BanManagerKind.Integrated)
            .SingleInstance();

        builder.RegisterAssemblyTypes(ThisAssembly)
            .Where(t => t.GetCustomAttributes<CoinFamilyAttribute>().Any() && t.GetInterfaces()
                .Any(i =>
                    i.IsAssignableFrom(typeof(IMiningPool)) ||
                    i.IsAssignableFrom(typeof(IPayoutHandler)) ||
                    i.IsAssignableFrom(typeof(IPayoutScheme))))
            .WithMetadataFrom<CoinFamilyAttribute>()
            .AsImplementedInterfaces();

        builder.RegisterAssemblyTypes(ThisAssembly)
            .Where(t => t.GetCustomAttributes<IdentifierAttribute>().Any() &&
                t.GetInterfaces().Any(i => i.IsAssignableFrom(typeof(IHashAlgorithm))))
            .Named<IHashAlgorithm>(t => t.GetCustomAttributes<IdentifierAttribute>().First().Name)
            .PropertiesAutowired();

        builder.RegisterAssemblyTypes(ThisAssembly)
            .Where(t => t.GetCustomAttributes<IdentifierAttribute>().Any() &&
                t.GetInterfaces().Any(i => i.IsAssignableFrom(typeof(IEthashLight))))
            .Named<IEthashLight>(t => t.GetCustomAttributes<IdentifierAttribute>().First().Name)
            .PropertiesAutowired();

        builder.RegisterAssemblyTypes(ThisAssembly)
            .Where(t => t.GetCustomAttributes<IdentifierAttribute>().Any() &&
                t.GetInterfaces().Any(i => i.IsAssignableFrom(typeof(IProgpowLight))))
            .Named<IProgpowLight>(t => t.GetCustomAttributes<IdentifierAttribute>().First().Name)
            .PropertiesAutowired();

        builder.RegisterAssemblyTypes(ThisAssembly)
            .Where(t => t.IsAssignableTo<EquihashSolver>())
            .PropertiesAutowired()
            .AsSelf();

        builder.RegisterAssemblyTypes(ThisAssembly)
            .Where(t => t.IsAssignableTo<ControllerBase>())
            .PropertiesAutowired()
            .AsSelf();

        builder.RegisterType<WebSocketNotificationsRelay>()
            .PropertiesAutowired()
            .AsSelf()
            .SingleInstance();

        builder.RegisterType<NicehashService>()
            .SingleInstance();

        builder.RegisterType<PushoverClient>()
            .SingleInstance();

        //////////////////////
        // Background services

        builder.Register(ctx =>
        {
            var config = ctx.Resolve<ClusterConfig>();
            var eventPipeline = config.EventPipeline;
            var handoff = eventPipeline?.Handoff;

            return new InMemoryShareEventQueue(new ShareEventHandoffOptions
            {
                SoftMaxBufferedEvents = handoff?.SoftMaxBufferedEvents ?? 100000,
                SoftMaxBufferedBytes = handoff?.SoftMaxBufferedBytes ?? 268435456,
                CriticalBufferedEvents = handoff?.CriticalBufferedEvents ?? 500000,
                CriticalBufferedBytes = handoff?.CriticalBufferedBytes ?? 1073741824
            });
        })
        .As<IShareEventQueue>()
        .SingleInstance();

        builder.Register(ctx =>
        {
            var eventPipeline = ctx.Resolve<ClusterConfig>().EventPipeline;
            var outbox = eventPipeline?.Outbox ?? new EventPipelineOutboxConfig();
            var broker = eventPipeline?.Broker;
            var batching = eventPipeline?.Batching;

            return new ShareEventOutboxOptions
            {
                Directory = outbox.Directory,
                SegmentMaxBytes = outbox.SegmentMaxBytes,
                WriterFlushEvents = outbox.WriterFlushEvents,
                WriterFlushBytes = outbox.WriterFlushBytes,
                WriterFlushMs = outbox.WriterFlushMs,
                FsyncMode = outbox.FsyncMode,
                FsyncIntervalMs = outbox.FsyncIntervalMs,
                SoftBacklogBytes = outbox.SoftBacklogBytes,
                CriticalBacklogBytes = outbox.CriticalBacklogBytes,
                PublisherMaxEvents = batching?.MaxEvents ?? 256,
                PublisherMaxApproxBytes = batching?.MaxApproxBytes ?? 524288,
                PublishRetryDelayMs = broker?.PublishRetryDelayMs ?? 250,
                PublishMaxRetryDelayMs = broker?.PublishMaxRetryDelayMs ?? 5000
            };
        }).SingleInstance();

        builder.Register(ctx =>
        {
            var options = ctx.Resolve<ShareEventOutboxOptions>();
            ShareEventOutboxRecovery.Recover(options.Directory);
            return new FileShareEventOutbox(options);
        })
        .As<IShareEventOutbox>()
        .SingleInstance();

        builder.Register(ctx =>
        {
            var config = ctx.Resolve<ClusterConfig>();
            var eventPipeline = config.EventPipeline;
            return new ShareBatchPublisherOptions
            {
                ProducerId = eventPipeline?.Broker?.ProducerId ?? Environment.MachineName,
                NodeId = config.InstanceId?.ToString() ?? Environment.MachineName,
                ClusterName = config.ClusterName ?? string.Empty,
                StreamName = eventPipeline?.Broker?.StreamName ?? "hashstorm:share-events",
                BrokerType = eventPipeline?.Broker?.Type ?? "none",
                MaxEvents = eventPipeline?.Batching?.MaxEvents ?? 256,
                MaxApproxBytes = eventPipeline?.Batching?.MaxApproxBytes ?? 524288,
                MaxDelayMs = eventPipeline?.Batching?.MaxDelayMs ?? 1000,
                PublishRetryDelayMs = eventPipeline?.Broker?.PublishRetryDelayMs ?? 250,
                PublishMaxRetryDelayMs = eventPipeline?.Broker?.PublishMaxRetryDelayMs ?? 5000
            };
        }).SingleInstance();

        builder.Register(ctx =>
        {
            var eventPipeline = ctx.Resolve<ClusterConfig>().EventPipeline;

            if(eventPipeline?.Enabled == true)
            {
                if(string.Equals(eventPipeline.Broker?.Type, "redis-streams", StringComparison.OrdinalIgnoreCase))
                    return (IShareEventBatchTransport) new RedisStreamsShareEventBatchTransport(eventPipeline.Broker, eventPipeline.Retention);

                throw new InvalidOperationException(
                    $"Unsupported eventPipeline.broker.type '{eventPipeline.Broker?.Type}'. eventPipeline.enabled=true requires a supported broker transport.");
            }

            return new NullShareEventBatchTransport();
        })
        .As<IShareEventBatchTransport>()
        .SingleInstance();

        builder.RegisterType<ShareEventOutboxWriter>()
            .SingleInstance();

        builder.RegisterType<ShareEventOutboxPublisher>()
            .SingleInstance();

        builder.RegisterType<PayoutManager>()
            .SingleInstance();

        builder.RegisterType<ShareRecorder>()
            .SingleInstance();

        builder.RegisterType<ShareReceiver>()
            .SingleInstance();

        builder.RegisterType<BtStreamReceiver>()
            .SingleInstance();

        builder.RegisterType<ShareRelay>()
            .SingleInstance();

        builder.RegisterType<StatsRecorder>()
            .SingleInstance();

        builder.RegisterType<NotificationService>()
            .SingleInstance();

        builder.RegisterType<MetricsPublisher>()
            .SingleInstance();

        //////////////////////
        // Payment Schemes

        builder.RegisterType<PPLNSPaymentScheme>()
            .Keyed<IPayoutScheme>(PayoutScheme.PPLNS)
            .SingleInstance();

        builder.RegisterType<PPLNSBFPaymentScheme>()
            .Keyed<IPayoutScheme>(PayoutScheme.PPLNSBF)
            .SingleInstance();

        builder.RegisterType<SOLOPaymentScheme>()
            .Keyed<IPayoutScheme>(PayoutScheme.SOLO)
            .SingleInstance();

        builder.RegisterType<PROPPaymentScheme>()
            .Keyed<IPayoutScheme>(PayoutScheme.PROP)
            .SingleInstance();

        //////////////////////
        // Alephium

        builder.RegisterType<AlephiumJobManager>();

        //////////////////////
        // Beam

        builder.RegisterType<BeamJobManager>();

        //////////////////////
        // Bitcoin and family

        builder.RegisterType<BitcoinJobManager>();

        //////////////////////
        // Conceal

        builder.RegisterType<ConcealJobManager>();

        //////////////////////
        // Cryptonote

        builder.RegisterType<CryptonoteJobManager>();

        //////////////////////
        // ZCash

        builder.RegisterType<EquihashJobManager>();

        //////////////////////
        // Ergo

        builder.RegisterType<ErgoJobManager>();

        //////////////////////
        // Ethereum

        builder.RegisterType<EthereumJobManager>();

        //////////////////////
        // Handshake

        builder.RegisterType<HandshakeJobManager>();

        //////////////////////
        // Kaspa

        builder.RegisterType<KaspaJobManager>();

        //////////////////////
        // Nexa

        builder.RegisterType<NexaJobManager>();

        //////////////////////
        // Progpow

        builder.RegisterType<ProgpowJobManager>();

        //////////////////////
        // Satoshicash

        builder.RegisterType<SatoshicashJobManager>();

        //////////////////////
        // Warthog

        builder.RegisterType<WarthogJobManager>();

        //////////////////////
        // Xelis

        builder.RegisterType<XelisJobManager>();

        //////////////////////
        // Zano

        builder.RegisterType<ZanoJobManager>();

        //////////////////////
        // MiningPoolRegistry: single instance, auto-start to attach bus listener
        builder.RegisterType<MiningPoolRegistry>()
            .AsSelf()
            .SingleInstance()
            .AutoActivate();

        base.Load(builder);
    }
}
