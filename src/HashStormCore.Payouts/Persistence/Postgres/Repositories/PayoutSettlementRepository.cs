using System.Data;
using Dapper;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Persistence.Postgres.Repositories;

public class PayoutSettlementRepository : IPayoutSettlementRepository
{
    private const string PaymentDebitUsage = "Balance reset after payment";

    public async Task<PayoutSettlementAttemptCandidate[]> GetAcceptedAttemptsForSettlementAsync(IDbConnection con,
        IDbTransaction tx, string poolId, int limit, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);
        RequireText(poolId, nameof(poolId));

        if(limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "Accepted payout settlement query limit must be greater than zero");

        const string query = @"WITH locked_batches AS (
                SELECT b.*
                FROM payout_batches b
                WHERE b.poolid = @poolid
                  AND b.state = ANY(@batchstates)
                  AND EXISTS (
                      SELECT 1
                      FROM payout_send_attempts psa
                      JOIN payout_attempt_intents pai ON pai.attemptid = psa.id
                          AND pai.batchid = psa.batchid
                          AND pai.poolid = psa.poolid
                          AND pai.coin = psa.coin
                          AND pai.state = @acceptedmapping
                      JOIN payout_intents pi ON pi.id = pai.intentid
                          AND pi.batchid = pai.batchid
                          AND pi.poolid = pai.poolid
                          AND pi.coin = pai.coin
                      WHERE psa.batchid = b.id
                        AND psa.poolid = b.poolid
                        AND psa.coin = b.coin
                        AND psa.state = @acceptedattempt
                        AND pi.state = @submitted
                        AND pi.paymentid IS NULL
                        AND pi.balancechangeid IS NULL
                        AND NOT EXISTS (
                            SELECT 1
                            FROM payout_attempt_intents blocking_pai
                            WHERE blocking_pai.attemptid = psa.id
                              AND blocking_pai.batchid = psa.batchid
                              AND blocking_pai.poolid = psa.poolid
                              AND blocking_pai.coin = psa.coin
                              AND blocking_pai.state <> @acceptedmapping
                        )
                        AND (
                            SELECT COUNT(*)
                            FROM (
                                SELECT DISTINCT pec.kind, pec.value
                                FROM payout_external_confirmations pec
                                WHERE pec.batchid = psa.batchid
                                  AND pec.attemptid = psa.id
                                  AND pec.poolid = psa.poolid
                                  AND pec.coin = psa.coin
                                  AND pec.kind = ANY(@evidencekinds)
                                  AND pec.value IS NOT NULL
                                  AND btrim(pec.value) <> ''
                            ) evidence_values
                        ) = 1
                  )
                ORDER BY b.updated, b.id
                LIMIT @limit
                FOR UPDATE OF b SKIP LOCKED
            )
            SELECT
                b.id AS BatchId,
                psa.id AS AttemptId,
                psa.poolid AS PoolId,
                psa.coin AS Coin,
                psa.method AS Method,
                evidence.evidencekind AS EvidenceKind,
                evidence.transactionconfirmationdata AS TransactionConfirmationData,
                psa.created AS Created,
                psa.updated AS Updated,
                counts.submittedintentcount AS SubmittedIntentCount,
                counts.unsettledsubmittedintentcount AS UnsettledSubmittedIntentCount,
                counts.settledintentcount AS SettledIntentCount
            FROM locked_batches b
            JOIN payout_send_attempts psa ON psa.batchid = b.id
                AND psa.poolid = b.poolid
                AND psa.coin = b.coin
                AND psa.state = @acceptedattempt
            JOIN LATERAL (
                SELECT
                    COUNT(*) FILTER (WHERE pi.state = @submitted) AS submittedintentcount,
                    COUNT(*) FILTER (
                        WHERE pi.state = @submitted
                          AND pi.paymentid IS NULL
                          AND pi.balancechangeid IS NULL
                    ) AS unsettledsubmittedintentcount,
                    COUNT(*) FILTER (WHERE pi.state = @settled) AS settledintentcount
                FROM payout_attempt_intents pai
                JOIN payout_intents pi ON pi.id = pai.intentid
                    AND pi.batchid = pai.batchid
                    AND pi.poolid = pai.poolid
                    AND pi.coin = pai.coin
                WHERE pai.attemptid = psa.id
                  AND pai.batchid = psa.batchid
                  AND pai.poolid = psa.poolid
                  AND pai.coin = psa.coin
                  AND pai.state = @acceptedmapping
            ) counts ON counts.unsettledsubmittedintentcount > 0
            JOIN LATERAL (
                SELECT MIN(kind) AS evidencekind, MIN(value) AS transactionconfirmationdata, COUNT(*) AS evidencecount
                FROM (
                    SELECT DISTINCT pec.kind, pec.value
                    FROM payout_external_confirmations pec
                    WHERE pec.batchid = psa.batchid
                      AND pec.attemptid = psa.id
                      AND pec.poolid = psa.poolid
                      AND pec.coin = psa.coin
                      AND pec.kind = ANY(@evidencekinds)
                      AND pec.value IS NOT NULL
                      AND btrim(pec.value) <> ''
                ) evidence_values
            ) evidence ON evidence.evidencecount = 1
            WHERE NOT EXISTS (
                SELECT 1
                FROM payout_attempt_intents blocking_pai
                WHERE blocking_pai.attemptid = psa.id
                  AND blocking_pai.batchid = psa.batchid
                  AND blocking_pai.poolid = psa.poolid
                  AND blocking_pai.coin = psa.coin
                  AND blocking_pai.state <> @acceptedmapping
            )
            ORDER BY psa.updated, psa.id
            LIMIT @limit";

        return (await con.QueryAsync<PayoutSettlementAttemptCandidate>(new CommandDefinition(query, new
        {
            poolid = poolId,
            batchstates = new[]
            {
                PayoutBatchStates.Sending,
                PayoutBatchStates.Submitted,
                PayoutBatchStates.AmbiguousRequiresReview
            },
            acceptedattempt = PayoutSendAttemptStates.Accepted,
            acceptedmapping = PayoutAttemptIntentStates.Accepted,
            submitted = PayoutIntentStates.Submitted,
            settled = PayoutIntentStates.Settled,
            evidencekinds = new[]
            {
                PayoutExternalConfirmationKinds.TxId,
                PayoutExternalConfirmationKinds.RawHash
            },
            limit
        }, tx, cancellationToken: ct))).ToArray();
    }

    public async Task<PayoutSettlementResult> SettleAcceptedAttemptAsync(IDbConnection con, IDbTransaction tx,
        PayoutSettlementRequest request, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);
        ValidateRequest(request);

        var batch = await LoadBatchForUpdateAsync(con, tx, request.BatchId, request.PoolId, ct);
        if(batch == null)
            return PayoutSettlementResult.AttemptNotEligible(request.BatchId, request.AttemptId);

        await LockBatchAttemptsAsync(con, tx, batch.Id, batch.PoolId, batch.Coin, ct);

        var attempt = await LoadAttemptForUpdateAsync(con, tx, request.AttemptId, batch, ct);
        if(attempt == null || attempt.State != PayoutSendAttemptStates.Accepted)
            return PayoutSettlementResult.AttemptNotEligible(request.BatchId, request.AttemptId);

        var mappings = await LoadAttemptMappingsForUpdateAsync(con, tx, attempt.Id, batch, ct);
        if(mappings.Length == 0)
            throw new InvalidOperationException("Accepted payout attempt has no intent mappings");

        if(mappings.Any(x => x.State != PayoutAttemptIntentStates.Accepted))
            return PayoutSettlementResult.AttemptNotEligible(request.BatchId, request.AttemptId);

        var intents = await LoadAttemptIntentsForUpdateAsync(con, tx, attempt.Id, batch, ct);
        if(intents.Length != mappings.Length)
            throw new InvalidOperationException("Accepted payout attempt mappings do not match payout intents");

        ValidateIntentLinkConsistency(intents);

        var submittedIntents = intents
            .Where(x => x.State == PayoutIntentStates.Submitted)
            .OrderBy(x => x.Id)
            .ToArray();

        var settledIntents = intents
            .Where(x => x.State == PayoutIntentStates.Settled)
            .OrderBy(x => x.Id)
            .ToArray();
        var settledConfirmationData = GetSettledConfirmationData(settledIntents);

        if(submittedIntents.Length + settledIntents.Length != intents.Length)
            return PayoutSettlementResult.AttemptNotEligible(request.BatchId, request.AttemptId);

        if(settledIntents.Length == intents.Length)
            return PayoutSettlementResult.AlreadySettled(batch.Id, attempt.Id, settledIntents.Select(x => x.Id).ToArray(),
                settledIntents.Select(x => x.PaymentId.Value).ToArray(),
                settledIntents.Select(x => x.BalanceChangeId.Value).ToArray(),
                settledConfirmationData);

        var transactionConfirmationData = await GetSettlementConfirmationAsync(con, tx, batch.Id, attempt.Id,
            batch.PoolId, batch.Coin, request.ExpectedEvidenceKind, ct);
        if(string.IsNullOrWhiteSpace(transactionConfirmationData))
            return PayoutSettlementResult.InsufficientEvidence(batch.Id, attempt.Id);
        if(settledConfirmationData != null && settledConfirmationData != transactionConfirmationData)
            throw new InvalidOperationException("Settled payout intent confirmation conflicts with settlement evidence");

        if(!await HasSufficientAggregateBalancesAsync(con, tx, submittedIntents, ct))
            return PayoutSettlementResult.InsufficientBalance(batch.Id, attempt.Id);

        var paymentIds = new List<long>(submittedIntents.Length);
        var balanceChangeIds = new List<long>(submittedIntents.Length);
        var settledIntentIds = new List<long>(intents.Length);

        settledIntentIds.AddRange(settledIntents.Select(x => x.Id));
        paymentIds.AddRange(settledIntents.Select(x => x.PaymentId.Value));
        balanceChangeIds.AddRange(settledIntents.Select(x => x.BalanceChangeId.Value));

        foreach(var intent in submittedIntents)
        {
            var paymentId = await InsertPaymentAsync(con, tx, intent, transactionConfirmationData, request.SettledAt, ct);
            var balanceChangeId = await InsertBalanceChangeAsync(con, tx, intent, request.SettledAt, ct);
            await DebitBalanceAsync(con, tx, intent, request.SettledAt, ct);
            await MarkIntentSettledAsync(con, tx, intent, paymentId, balanceChangeId, transactionConfirmationData,
                request.SettledAt, ct);

            paymentIds.Add(paymentId);
            balanceChangeIds.Add(balanceChangeId);
            settledIntentIds.Add(intent.Id);
        }

        if(await CountUnsettledBatchIntentsAsync(con, tx, batch.Id, batch.PoolId, batch.Coin, ct) == 0)
            await MarkBatchSettledAsync(con, tx, batch, request.SettledAt, ct);

        return PayoutSettlementResult.Settled(batch.Id, attempt.Id, settledIntentIds.OrderBy(x => x).ToArray(),
            paymentIds.ToArray(), balanceChangeIds.ToArray(), transactionConfirmationData);
    }

    private static async Task<Entities.PayoutBatch> LoadBatchForUpdateAsync(IDbConnection con, IDbTransaction tx,
        long batchId, string poolId, CancellationToken ct)
    {
        const string query = @"SELECT * FROM payout_batches
            WHERE id = @batchid AND poolid = @poolid
            FOR UPDATE";

        return await con.QuerySingleOrDefaultAsync<Entities.PayoutBatch>(new CommandDefinition(query, new
        {
            batchid = batchId,
            poolid = poolId
        }, tx, cancellationToken: ct));
    }

    private static async Task LockBatchAttemptsAsync(IDbConnection con, IDbTransaction tx, long batchId, string poolId,
        string coin, CancellationToken ct)
    {
        const string query = @"SELECT id FROM payout_send_attempts
            WHERE batchid = @batchid AND poolid = @poolid AND coin = @coin
            ORDER BY id
            FOR UPDATE";

        await con.QueryAsync<long>(new CommandDefinition(query, new
        {
            batchid = batchId,
            poolid = poolId,
            coin
        }, tx, cancellationToken: ct));
    }

    private static async Task<Entities.PayoutSendAttempt> LoadAttemptForUpdateAsync(IDbConnection con, IDbTransaction tx,
        long attemptId, Entities.PayoutBatch batch, CancellationToken ct)
    {
        const string query = @"SELECT * FROM payout_send_attempts
            WHERE id = @attemptid AND batchid = @batchid AND poolid = @poolid AND coin = @coin
            FOR UPDATE";

        return await con.QuerySingleOrDefaultAsync<Entities.PayoutSendAttempt>(new CommandDefinition(query, new
        {
            attemptid = attemptId,
            batchid = batch.Id,
            poolid = batch.PoolId,
            coin = batch.Coin
        }, tx, cancellationToken: ct));
    }

    private static async Task<Entities.PayoutAttemptIntent[]> LoadAttemptMappingsForUpdateAsync(IDbConnection con,
        IDbTransaction tx, long attemptId, Entities.PayoutBatch batch, CancellationToken ct)
    {
        const string query = @"SELECT * FROM payout_attempt_intents
            WHERE attemptid = @attemptid AND batchid = @batchid AND poolid = @poolid AND coin = @coin
            ORDER BY intentid
            FOR UPDATE";

        return (await con.QueryAsync<Entities.PayoutAttemptIntent>(new CommandDefinition(query, new
        {
            attemptid = attemptId,
            batchid = batch.Id,
            poolid = batch.PoolId,
            coin = batch.Coin
        }, tx, cancellationToken: ct))).ToArray();
    }

    private static async Task<Entities.PayoutIntent[]> LoadAttemptIntentsForUpdateAsync(IDbConnection con,
        IDbTransaction tx, long attemptId, Entities.PayoutBatch batch, CancellationToken ct)
    {
        const string query = @"SELECT pi.*
            FROM payout_intents pi
            JOIN payout_attempt_intents pai ON pai.intentid = pi.id
                AND pai.batchid = pi.batchid
                AND pai.poolid = pi.poolid
                AND pai.coin = pi.coin
            WHERE pai.attemptid = @attemptid
              AND pai.batchid = @batchid
              AND pai.poolid = @poolid
              AND pai.coin = @coin
            ORDER BY pi.id
            FOR UPDATE OF pi";

        return (await con.QueryAsync<Entities.PayoutIntent>(new CommandDefinition(query, new
        {
            attemptid = attemptId,
            batchid = batch.Id,
            poolid = batch.PoolId,
            coin = batch.Coin
        }, tx, cancellationToken: ct))).ToArray();
    }

    private static void ValidateIntentLinkConsistency(IReadOnlyCollection<Entities.PayoutIntent> intents)
    {
        foreach(var intent in intents)
        {
            var hasPayment = intent.PaymentId.HasValue;
            var hasBalanceChange = intent.BalanceChangeId.HasValue;
            var hasConfirmation = !string.IsNullOrWhiteSpace(intent.TransactionConfirmationData);
            var hasSettledAt = intent.Settled.HasValue;

            if(intent.State == PayoutIntentStates.Settled)
            {
                if(!hasPayment || !hasBalanceChange || !hasConfirmation || !hasSettledAt)
                    throw new InvalidOperationException("Settled payout intent is missing accounting links");
            }

            else
            {
                if(hasPayment || hasBalanceChange || hasConfirmation || hasSettledAt)
                    throw new InvalidOperationException("Unsettled payout intent has accounting links");
            }
        }
    }

    private static async Task<bool> HasSufficientAggregateBalancesAsync(IDbConnection con, IDbTransaction tx,
        IReadOnlyCollection<Entities.PayoutIntent> submittedIntents, CancellationToken ct)
    {
        var requiredBalances = submittedIntents
            .GroupBy(x => new { x.PoolId, x.Address })
            .Select(x => new RequiredBalance(x.Key.PoolId, x.Key.Address, x.Sum(y => y.Amount)))
            .OrderBy(x => x.PoolId, StringComparer.Ordinal)
            .ThenBy(x => x.Address, StringComparer.Ordinal)
            .ToArray();

        foreach(var requiredBalance in requiredBalances)
        {
            var balance = await LoadBalanceForUpdateAsync(con, tx, requiredBalance.PoolId, requiredBalance.Address, ct);
            if(balance == null || balance.Amount < requiredBalance.Amount)
                return false;
        }

        return true;
    }

    private static string GetSettledConfirmationData(IReadOnlyCollection<Entities.PayoutIntent> settledIntents)
    {
        var values = settledIntents
            .Select(x => x.TransactionConfirmationData)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if(values.Length > 1)
            throw new InvalidOperationException("Settled payout intents have conflicting transaction confirmations");

        return values.SingleOrDefault();
    }

    private static async Task<string> GetSettlementConfirmationAsync(IDbConnection con, IDbTransaction tx, long batchId,
        long attemptId, string poolId, string coin, string expectedEvidenceKind, CancellationToken ct)
    {
        const string query = @"SELECT DISTINCT value FROM payout_external_confirmations
            WHERE batchid = @batchid
              AND attemptid = @attemptid
              AND poolid = @poolid
              AND coin = @coin
              AND kind = @kind
            ORDER BY value";

        var values = (await con.QueryAsync<string>(new CommandDefinition(query, new
        {
            batchid = batchId,
            attemptid = attemptId,
            poolid = poolId,
            coin,
            kind = expectedEvidenceKind
        }, tx, cancellationToken: ct))).ToArray();

        return values.Length == 1 ? values[0] : null;
    }

    private static async Task<BalanceRow> LoadBalanceForUpdateAsync(IDbConnection con, IDbTransaction tx,
        string poolId, string address, CancellationToken ct)
    {
        const string query = @"SELECT * FROM balances
            WHERE poolid = @poolid AND address = @address
            FOR UPDATE";

        return await con.QuerySingleOrDefaultAsync<BalanceRow>(new CommandDefinition(query, new
        {
            poolid = poolId,
            address
        }, tx, cancellationToken: ct));
    }

    private static Task<long> InsertPaymentAsync(IDbConnection con, IDbTransaction tx, Entities.PayoutIntent intent,
        string transactionConfirmationData, DateTime settledAt, CancellationToken ct)
    {
        const string query = @"INSERT INTO payments(poolid, coin, address, amount, transactionconfirmationdata, created)
            VALUES(@poolid, @coin, @address, @amount, @transactionconfirmationdata, @created)
            RETURNING id";

        return con.QuerySingleAsync<long>(new CommandDefinition(query, new
        {
            poolid = intent.PoolId,
            coin = intent.Coin,
            address = intent.Address,
            amount = intent.Amount,
            transactionconfirmationdata = transactionConfirmationData,
            created = settledAt
        }, tx, cancellationToken: ct));
    }

    private static Task<long> InsertBalanceChangeAsync(IDbConnection con, IDbTransaction tx,
        Entities.PayoutIntent intent, DateTime settledAt, CancellationToken ct)
    {
        const string query = @"INSERT INTO balance_changes(poolid, address, amount, usage, tags, created)
            VALUES(@poolid, @address, @amount, @usage, NULL, @created)
            RETURNING id";

        return con.QuerySingleAsync<long>(new CommandDefinition(query, new
        {
            poolid = intent.PoolId,
            address = intent.Address,
            amount = -intent.Amount,
            usage = PaymentDebitUsage,
            created = settledAt
        }, tx, cancellationToken: ct));
    }

    private static async Task DebitBalanceAsync(IDbConnection con, IDbTransaction tx, Entities.PayoutIntent intent,
        DateTime settledAt, CancellationToken ct)
    {
        const string query = @"UPDATE balances
            SET amount = amount - @amount, updated = @updated
            WHERE poolid = @poolid AND address = @address AND amount >= @amount";

        var rows = await con.ExecuteAsync(new CommandDefinition(query, new
        {
            poolid = intent.PoolId,
            address = intent.Address,
            amount = intent.Amount,
            updated = settledAt
        }, tx, cancellationToken: ct));

        EnsureRowCount(rows, 1, "balance debit for payout settlement");
    }

    private static async Task MarkIntentSettledAsync(IDbConnection con, IDbTransaction tx, Entities.PayoutIntent intent,
        long paymentId, long balanceChangeId, string transactionConfirmationData, DateTime settledAt, CancellationToken ct)
    {
        const string query = @"UPDATE payout_intents
            SET state = @settled, paymentid = @paymentid, balancechangeid = @balancechangeid,
                transactionconfirmationdata = @transactionconfirmationdata, settled = @settledat, updated = @settledat
            WHERE id = @intentid
              AND batchid = @batchid
              AND poolid = @poolid
              AND coin = @coin
              AND state = @submitted
              AND paymentid IS NULL
              AND balancechangeid IS NULL";

        var rows = await con.ExecuteAsync(new CommandDefinition(query, new
        {
            intentid = intent.Id,
            batchid = intent.BatchId,
            poolid = intent.PoolId,
            coin = intent.Coin,
            submitted = PayoutIntentStates.Submitted,
            settled = PayoutIntentStates.Settled,
            paymentid = paymentId,
            balancechangeid = balanceChangeId,
            transactionconfirmationdata = transactionConfirmationData,
            settledat = settledAt
        }, tx, cancellationToken: ct));

        EnsureRowCount(rows, 1, "submitted payout intent for settlement");
    }

    private static Task<int> CountUnsettledBatchIntentsAsync(IDbConnection con, IDbTransaction tx, long batchId,
        string poolId, string coin, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM payout_intents
            WHERE batchid = @batchid AND poolid = @poolid AND coin = @coin AND state <> @settled";

        return con.QuerySingleAsync<int>(new CommandDefinition(query, new
        {
            batchid = batchId,
            poolid = poolId,
            coin,
            settled = PayoutIntentStates.Settled
        }, tx, cancellationToken: ct));
    }

    private static async Task MarkBatchSettledAsync(IDbConnection con, IDbTransaction tx, Entities.PayoutBatch batch,
        DateTime settledAt, CancellationToken ct)
    {
        const string query = @"UPDATE payout_batches
            SET state = @settled, settled = @settledat, updated = @settledat
            WHERE id = @batchid AND poolid = @poolid AND coin = @coin AND state <> @settled";

        var rows = await con.ExecuteAsync(new CommandDefinition(query, new
        {
            batchid = batch.Id,
            poolid = batch.PoolId,
            coin = batch.Coin,
            settled = PayoutBatchStates.Settled,
            settledat = settledAt
        }, tx, cancellationToken: ct));

        EnsureRowCount(rows, 1, "payout batch for final settlement");
    }

    private static void ValidateRequest(PayoutSettlementRequest request)
    {
        if(request == null)
            throw new ArgumentNullException(nameof(request));

        RequireText(request.PoolId, nameof(request.PoolId));
        RequireText(request.ExpectedEvidenceKind, nameof(request.ExpectedEvidenceKind));

        if(request.BatchId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.BatchId), "Payout batch id must be greater than zero");

        if(request.AttemptId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.AttemptId), "Payout send attempt id must be greater than zero");

        if(request.ExpectedEvidenceKind != PayoutExternalConfirmationKinds.TxId &&
           request.ExpectedEvidenceKind != PayoutExternalConfirmationKinds.RawHash)
            throw new ArgumentException("Settlement expected evidence kind must be txid or raw_hash",
                nameof(request.ExpectedEvidenceKind));
    }

    private static void EnsureRowCount(int actual, int expected, string description)
    {
        if(actual != expected)
            throw new InvalidOperationException($"Expected {expected} row(s) for {description} but updated {actual}");
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

    private record RequiredBalance(string PoolId, string Address, decimal Amount);

    private record BalanceRow(decimal Amount);
}
