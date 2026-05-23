namespace HashStormCore.PayoutProcessor.Configuration;

public enum PayoutProcessorMode
{
    Disabled,
    DryRun,
    DbMutating
}

public class PayoutProcessorConfig
{
    public bool Enabled { get; set; } = false;
    public PayoutProcessorMode Mode { get; set; } = PayoutProcessorMode.Disabled;
    public string PostgresConnectionString { get; set; } = string.Empty;
    public string CoinsJsonPath { get; set; } = string.Empty;
    public string[] Pools { get; set; } = Array.Empty<string>();
    public int ReservationIntervalSeconds { get; set; } = 60;
    public int PlanningIntervalSeconds { get; set; } = 15;
    public int ExecutionIntervalSeconds { get; set; } = 5;
    public int StaleReconciliationIntervalSeconds { get; set; } = 60;
    public int OperationIdReconciliationIntervalSeconds { get; set; } = 30;
    public int SettlementIntervalSeconds { get; set; } = 15;
    public int PlanningMaxBatches { get; set; } = 8;
    public int ReservationMaxCandidates { get; set; } = 500;
    public int ExecutionBatchSize { get; set; } = 8;
    public int StaleSendingAgeSeconds { get; set; } = 900;
    public bool FakeAdaptersOnly { get; set; } = true;
}
