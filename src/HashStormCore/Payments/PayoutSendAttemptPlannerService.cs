using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Payments;

public class PayoutSendAttemptPlannerService
{
    public PayoutSendAttemptPlannerService(IPayoutIntentRepository payoutIntentRepo)
    {
        this.payoutIntentRepo = payoutIntentRepo ?? throw new ArgumentNullException(nameof(payoutIntentRepo));
    }

    private const string HashDomain = "HashStormCore:payout-send-attempt:v1";
    private readonly IPayoutIntentRepository payoutIntentRepo;

    public async Task<CreatePayoutSendAttemptsResult> CreateSendAttemptsAsync(IDbConnection con, IDbTransaction tx,
        CreatePayoutSendAttemptsRequest request, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);
        ValidateRequest(request);

        var batch = await payoutIntentRepo.GetBatchForUpdateAsync(con, tx, request.BatchId, request.PoolId, request.Coin, ct);
        if(batch == null)
            return CreatePayoutSendAttemptsResult.BatchNotFound();

        if(batch.SendShape != request.SendShape)
            throw new InvalidOperationException("Payout send attempt request send shape does not match the reserved batch");

        if(batch.State != PayoutBatchStates.Reserved)
            return CreatePayoutSendAttemptsResult.BatchNotReserved(batch);

        var existingAttemptCount = await payoutIntentRepo.GetSendAttemptCountForBatchAsync(con, tx, request.BatchId,
            request.PoolId, request.Coin, ct);
        if(existingAttemptCount > 0)
            return CreatePayoutSendAttemptsResult.AttemptsAlreadyExist(batch);

        var reservedIntents = await payoutIntentRepo.GetReservedIntentsForBatchAsync(con, tx, request.BatchId,
            request.PoolId, request.Coin, ct);
        if(reservedIntents.Length == 0)
            return CreatePayoutSendAttemptsResult.NoReservedIntents(batch);

        var groups = CreateGroups(request, reservedIntents);
        var attempts = new List<PayoutSendAttempt>(groups.Count);

        for(var i = 0; i < groups.Count; i++)
        {
            var attemptNo = i + 1;
            var group = groups[i];
            var amountSnapshot = group.Sum(x => x.Amount);

            var attemptRequest = new CreatePayoutSendAttemptRequest
            {
                BatchId = batch.Id,
                PoolId = batch.PoolId,
                Coin = batch.Coin,
                AttemptNo = attemptNo,
                Method = request.Method,
                RequestHash = CreateRequestHash(request, attemptNo, group),
                RequestSummary = CreateRequestSummary(request, group.Length, amountSnapshot),
                RecipientCount = group.Length,
                AmountSnapshot = amountSnapshot,
                Created = request.Created
            };

            var attempt = await payoutIntentRepo.CreateSendAttemptAsync(con, tx, attemptRequest,
                group.Select(x => x.Id).ToArray(), ct);

            attempts.Add(attempt);
        }

        return CreatePayoutSendAttemptsResult.Created(batch, attempts);
    }

    private static List<PayoutIntent[]> CreateGroups(CreatePayoutSendAttemptsRequest request, PayoutIntent[] intents)
    {
        var orderedIntents = intents
            .OrderBy(x => x.Address, StringComparer.Ordinal)
            .ThenBy(x => x.Id)
            .ToArray();

        switch(request.SendShape)
        {
            case PayoutSendShapes.BatchMultiRecipient:
            case PayoutSendShapes.AsyncOperation:
                return new List<PayoutIntent[]> { orderedIntents };

            case PayoutSendShapes.PerAddress:
                return orderedIntents.Select(x => new[] { x }).ToList();

            case PayoutSendShapes.AddressGroup:
                return orderedIntents
                    .Select((intent, index) => new { intent, index })
                    .GroupBy(x => x.index / request.MaxRecipientsPerAttempt)
                    .Select(x => x.Select(y => y.intent).ToArray())
                    .ToList();

            default:
                throw new ArgumentException($"Unsupported payout send shape '{request.SendShape}'", nameof(request));
        }
    }

    private static string CreateRequestHash(CreatePayoutSendAttemptsRequest request, int attemptNo, IReadOnlyCollection<PayoutIntent> intents)
    {
        var builder = new StringBuilder();
        builder.AppendLine(HashDomain);
        builder.Append("batchid=").AppendLine(request.BatchId.ToString(CultureInfo.InvariantCulture));
        builder.Append("poolid=").AppendLine(request.PoolId);
        builder.Append("coin=").AppendLine(request.Coin);
        builder.Append("sendshape=").AppendLine(request.SendShape);
        builder.Append("method=").AppendLine(request.Method);
        builder.Append("attemptindex=").AppendLine(attemptNo.ToString(CultureInfo.InvariantCulture));

        foreach(var intent in intents.OrderBy(x => x.Address, StringComparer.Ordinal).ThenBy(x => x.Id))
        {
            builder.Append("intentid=").Append(intent.Id.ToString(CultureInfo.InvariantCulture))
                .Append('\t').Append("address=").Append(intent.Address)
                .Append('\t').Append("amount=").Append(FormatDecimal(intent.Amount))
                .AppendLine();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreateRequestSummary(CreatePayoutSendAttemptsRequest request, int recipientCount, decimal amount)
    {
        return $"{request.SendShape}:{request.Method}:recipients={recipientCount.ToString(CultureInfo.InvariantCulture)}:amount={FormatDecimal(amount)}";
    }

    private static void ValidateRequest(CreatePayoutSendAttemptsRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        if(request.BatchId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.BatchId), "Payout batch id must be greater than zero");

        RequireText(request.PoolId, nameof(request.PoolId));
        RequireText(request.Coin, nameof(request.Coin));
        RequireText(request.SendShape, nameof(request.SendShape));
        RequireText(request.Method, nameof(request.Method));

        switch(request.SendShape)
        {
            case PayoutSendShapes.BatchMultiRecipient:
            case PayoutSendShapes.PerAddress:
            case PayoutSendShapes.AsyncOperation:
                break;

            case PayoutSendShapes.AddressGroup:
                if(request.MaxRecipientsPerAttempt <= 0)
                    throw new ArgumentOutOfRangeException(nameof(request.MaxRecipientsPerAttempt),
                        "Address-group payout planning requires a maximum recipient count greater than zero");
                break;

            default:
                throw new ArgumentException($"Unsupported payout send shape '{request.SendShape}'", nameof(request));
        }
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
