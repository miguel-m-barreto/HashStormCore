using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Model.Projections;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class PayoutReservationService
{
    public PayoutReservationService(IPayoutIntentRepository payoutIntentRepo, IPayoutReservationRepository payoutReservationRepo)
    {
        this.payoutIntentRepo = payoutIntentRepo ?? throw new ArgumentNullException(nameof(payoutIntentRepo));
        this.payoutReservationRepo = payoutReservationRepo ?? throw new ArgumentNullException(nameof(payoutReservationRepo));
    }

    private readonly IPayoutIntentRepository payoutIntentRepo;
    private readonly IPayoutReservationRepository payoutReservationRepo;

    public async Task<CreatePayoutReservationResult> CreateReservationAsync(IDbConnection con, IDbTransaction tx,
        CreatePayoutReservationRequest request, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);
        ValidateRequest(request);

        var activeBatch = await payoutIntentRepo.GetActiveBatchForPoolAsync(con, tx, request.PoolId, ct);
        if(activeBatch != null)
            return CreatePayoutReservationResult.ActiveBatchExists(activeBatch);

        var rewardRecipientThresholds = ResolveRewardRecipientThresholds(request);

        var candidates = await payoutReservationRepo.GetEligibleCandidatesAsync(con, tx, request.PoolId,
            request.MinimumPayment, request.MaxCandidates, ct, rewardRecipientThresholds);

        if(candidates.Length == 0)
            return CreatePayoutReservationResult.NoEligibleBalances();

        var batchRequest = new CreatePayoutBatchRequest
        {
            PoolId = request.PoolId,
            Coin = request.Coin,
            CoinFamily = request.CoinFamily,
            Handler = request.Handler,
            SendShape = request.SendShape,
            RecipientSetHash = CreateRecipientSetHash(request, candidates),
            MinimumAmount = request.MinimumPayment,
            ReservedAmountSnapshot = candidates.Sum(x => x.AvailableAmount),
            IntentCountSnapshot = candidates.Length,
            Created = request.Created
        };

        var intentRequests = candidates
            .OrderBy(x => x.Address, StringComparer.Ordinal)
            .Select(x => new CreatePayoutIntentRequest
            {
                Address = x.Address,
                Amount = x.AvailableAmount,
                BalanceSnapshotAmount = x.BalanceSnapshotAmount,
                BalanceSnapshotUpdated = x.BalanceSnapshotUpdated,
                PaymentThreshold = x.PaymentThreshold
            })
            .ToArray();

        var batch = await payoutIntentRepo.CreateReservedBatchAsync(con, tx, batchRequest, intentRequests, ct);
        return CreatePayoutReservationResult.Created(batch);
    }

    private static void ValidateRequest(CreatePayoutReservationRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        RequireText(request.PoolId, nameof(request.PoolId));
        RequireText(request.Coin, nameof(request.Coin));
        RequireText(request.CoinFamily, nameof(request.CoinFamily));
        RequireText(request.Handler, nameof(request.Handler));
        RequireText(request.SendShape, nameof(request.SendShape));

        switch(request.SendShape)
        {
            case PayoutSendShapes.BatchMultiRecipient:
            case PayoutSendShapes.PerAddress:
            case PayoutSendShapes.AddressGroup:
            case PayoutSendShapes.AsyncOperation:
                break;

            default:
                throw new ArgumentException($"Unsupported payout send shape '{request.SendShape}'", nameof(request));
        }

        if(request.MinimumPayment < 0)
            throw new ArgumentOutOfRangeException(nameof(request.MinimumPayment), "Minimum payment must be greater than or equal to zero");

        if(request.MaxCandidates <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.MaxCandidates), "Maximum candidate count must be greater than zero");

        ValidateRewardRecipientThresholds(request);
    }

    private static PayoutRewardRecipientThreshold[] ResolveRewardRecipientThresholds(CreatePayoutReservationRequest request)
    {
        if(request.RewardRecipientThresholds == null || request.RewardRecipientThresholds.Count == 0)
            return Array.Empty<PayoutRewardRecipientThreshold>();

        return request.RewardRecipientThresholds
            .Select(x => new PayoutRewardRecipientThreshold
            {
                Address = x.Address,
                MinimumPayment = x.MinimumPayment ?? request.MinimumPayment
            })
            .ToArray();
    }

    private static void ValidateRewardRecipientThresholds(CreatePayoutReservationRequest request)
    {
        if(request.RewardRecipientThresholds == null)
            return;

        foreach(var threshold in request.RewardRecipientThresholds)
        {
            if(threshold == null)
                throw new ArgumentException("Reward recipient threshold entry is required", nameof(request));

            RequireText(threshold.Address, nameof(threshold.Address));

            if(threshold.MinimumPayment.HasValue && threshold.MinimumPayment.Value < 0)
                throw new ArgumentOutOfRangeException(nameof(request),
                    "Reward recipient minimum payment must be greater than or equal to zero");
        }

        var duplicate = request.RewardRecipientThresholds
            .GroupBy(x => x.Address, StringComparer.Ordinal)
            .FirstOrDefault(x => x.Count() > 1);
        if(duplicate != null)
            throw new ArgumentException($"Duplicate reward recipient threshold address '{duplicate.Key}'", nameof(request));
    }

    private static string CreateRecipientSetHash(CreatePayoutReservationRequest request, IReadOnlyCollection<PayoutReservationCandidate> candidates)
    {
        var builder = new StringBuilder();
        builder.AppendLine("HashStormCore:payout-recipient-set:v1");
        builder.Append("poolid=").AppendLine(request.PoolId);
        builder.Append("coin=").AppendLine(request.Coin);
        builder.Append("minimum=").AppendLine(FormatDecimal(request.MinimumPayment));

        foreach(var candidate in candidates.OrderBy(x => x.Address, StringComparer.Ordinal))
        {
            builder.Append("address=").Append(candidate.Address)
                .Append('\t').Append("amount=").Append(FormatDecimal(candidate.AvailableAmount))
                .Append('\t').Append("balanceUpdated=").Append(candidate.BalanceSnapshotUpdated.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                .Append('\t').Append("threshold=").Append(FormatDecimal(candidate.PaymentThreshold))
                .AppendLine();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    private static void RequireText(string value, string name)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required", name);
    }

    private static IDbConnection RequireConnection(IDbConnection con)
    {
        return con ?? throw new ArgumentNullException(nameof(con));
    }

    private static IDbTransaction RequireTransaction(IDbTransaction tx)
    {
        return tx ?? throw new ArgumentNullException(nameof(tx));
    }
}
