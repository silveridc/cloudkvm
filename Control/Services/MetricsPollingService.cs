using System.Collections.Concurrent;
using Control.Interface;
using Control.Model.Options;
using Grpc.Core;
using Kvm.Contracts;

namespace Control.Services;

/// <summary>
/// 后台按固定间隔轮询所有配置节点的指标快照（有限并发），结果写入共享 MetricsStore；
/// 聚合读只从 store 取数，不在请求路径现场 fan-out。
/// </summary>
public sealed class MetricsPollingService(
    IClusterClientFactory clusterClientFactory,
    IOptionsMonitor<ClustersOptions> clustersOptions,
    IOptionsMonitor<ClusterMetricsOptions> metricsOptions,
    MetricsStore metricsStore,
    ILogger<MetricsPollingService> logger) : BackgroundService
{
    private const int CallTimeoutSeconds = 10;

    private const string UnimplementedError = "The cluster agent does not implement metrics collection; upgrade the cluster agent.";

    // 每个节点只在连续失败（或首次 Unimplemented）时记一次 Warning，之后降为 Debug，避免刷日志。
    private readonly ConcurrentDictionary<string, bool> _failingNodes = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(Math.Max(1, metricsOptions.CurrentValue.PollIntervalSeconds)));
        while (true)
        {
            await PollAllNodesAsync(stoppingToken);
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollAllNodesAsync(CancellationToken stoppingToken)
    {
        Dictionary<string, ClusterNodeOptions> nodes = clustersOptions.CurrentValue.Nodes;
        if (nodes.Count == 0)
        {
            return;
        }
        ClusterMetricsOptions options = metricsOptions.CurrentValue;
        int concurrency = Math.Clamp(options.MaxConcurrentPolls, 1, nodes.Count);
        using SemaphoreSlim gate = new(concurrency, concurrency);
        try
        {
            await Task.WhenAll(nodes.Keys.Select(async node =>
            {
                try
                {
                    await PollNodeAsync(node, gate, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
            }));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PollNodeAsync(string node, SemaphoreSlim gate, CancellationToken stoppingToken)
    {
        await gate.WaitAsync(stoppingToken);
        try
        {
            if (!clusterClientFactory.TryCreate(node, out ClusterAgent.ClusterAgentClient? client) || client is null)
            {
                RecordFailure(node, "The node address or RPC token is not configured.");
                return;
            }

            NodeMetricsReply reply;
            try
            {
                reply = await client.GetNodeMetricsAsync(
                    new NodeMetricsRequest(),
                    deadline: DateTime.UtcNow.AddSeconds(CallTimeoutSeconds),
                    cancellationToken: stoppingToken);
            }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.Unimplemented)
            {
                // 旧版 Cluster 没有指标 RPC：让节点保持离线降级，不崩进程、不刷日志。
                metricsStore.RecordFailure(node, UnimplementedError);
                if (_failingNodes.TryAdd(node, true))
                {
                    logger.LogWarning("The cluster agent for node {Node} does not implement metrics collection; node metrics stay disabled until the agent is upgraded.", node);
                }
                return;
            }
            catch (RpcException exception)
            {
                RecordFailure(node, exception.Status.Detail is { Length: > 0 } detail ? detail : exception.StatusCode.ToString());
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecordFailure(node, exception.Message);
                return;
            }

            metricsStore.RecordSuccess(node, reply, DateTimeOffset.UtcNow);
            if (_failingNodes.TryRemove(node, out _))
            {
                logger.LogInformation("Node {Node} responded to metrics collection again.", node);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void RecordFailure(string node, string error)
    {
        metricsStore.RecordFailure(node, error);
        if (_failingNodes.TryAdd(node, true))
        {
            logger.LogWarning("Unable to collect metrics from node {Node}: {Error}", node, error);
        }
        else
        {
            logger.LogDebug("Unable to collect metrics from node {Node}: {Error}", node, error);
        }
    }
}
