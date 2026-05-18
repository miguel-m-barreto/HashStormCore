using System.Data;
using Dapper;
using HashStormCore.Persistence.Model;
using HashStormCore.Persistence.Model.Projections;
using HashStormCore.Persistence.Repositories;

namespace HashStormCore.Persistence.Postgres.Repositories;

public class PayoutIntentRepository : IPayoutIntentRepository
{
    private static readonly string[] ActiveBatchStates =
    {
        PayoutBatchStates.Reserved,
        PayoutBatchStates.Sending,
        PayoutBatchStates.Submitted,
        PayoutBatchStates.AmbiguousRequiresReview
    };

    private static readonly string[] RecoverableBatchStates =
    {
        PayoutBatchStates.Reserved,
        PayoutBatchStates.Sending,
        PayoutBatchStates.Submitted,
        PayoutBatchStates.AmbiguousRequiresReview
    };

    private static readonly string[] ReservedAmountStates =
    {
        PayoutIntentStates.Reserved,
        PayoutIntentStates.Sending,
        PayoutIntentStates.Submitted
    };

    private static readonly string[] ActiveIntentStates =
    {
        PayoutIntentStates.Reserved,
        PayoutIntentStates.Sending,
        PayoutIntentStates.Submitted,
        PayoutIntentStates.AmbiguousRequiresReview
    };

    public async Task<PayoutBatch> CreateReservedBatchAsync(IDbConnection con, IDbTransaction tx, CreatePayoutBatchRequest batch,
        IReadOnlyCollection<CreatePayoutIntentRequest> intents, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        if(batch == null)
            throw new ArgumentNullException(nameof(batch));

        if(intents == null || intents.Count == 0)
            throw new ArgumentException("At least one payout intent is required", nameof(intents));

        if(batch.IntentCountSnapshot != intents.Count)
            throw new InvalidOperationException("Payout batch intent count snapshot does not match supplied intents");

        var reservedAmountSnapshot = intents.Sum(x => x.Amount);
        if(batch.ReservedAmountSnapshot != reservedAmountSnapshot)
            throw new InvalidOperationException("Payout batch reserved amount snapshot does not match supplied intents");

        const string insertBatchQuery = @"INSERT INTO payout_batches(poolid, coin, coinfamily, handler, state, sendshape,
                recipientsethash, minimumamount, reservedamountsnapshot, intentcountsnapshot, created, updated)
            VALUES(@poolid, @coin, @coinfamily, @handler, @state, @sendshape, @recipientsethash, @minimumamount,
                @reservedamountsnapshot, @intentcountsnapshot, @created, @updated)
            RETURNING *";

        var batchEntity = await con.QuerySingleAsync<Entities.PayoutBatch>(new CommandDefinition(insertBatchQuery, new
        {
            poolid = batch.PoolId,
            coin = batch.Coin,
            coinfamily = batch.CoinFamily,
            handler = batch.Handler,
            state = PayoutBatchStates.Reserved,
            sendshape = batch.SendShape,
            recipientsethash = batch.RecipientSetHash,
            minimumamount = batch.MinimumAmount,
            reservedamountsnapshot = batch.ReservedAmountSnapshot,
            intentcountsnapshot = batch.IntentCountSnapshot,
            created = batch.Created,
            updated = batch.Created
        }, tx, cancellationToken: ct));

        const string insertIntentQuery = @"INSERT INTO payout_intents(batchid, poolid, coin, address, state, amount,
                balancesnapshotamount, balancesnapshotupdated, paymentthreshold, created, updated)
            VALUES(@batchid, @poolid, @coin, @address, @state, @amount, @balancesnapshotamount,
                @balancesnapshotupdated, @paymentthreshold, @created, @updated)
            RETURNING *";

        var intentEntities = new List<Entities.PayoutIntent>(intents.Count);

        foreach(var intent in intents)
        {
            var intentEntity = await con.QuerySingleAsync<Entities.PayoutIntent>(new CommandDefinition(insertIntentQuery, new
            {
                batchid = batchEntity.Id,
                poolid = batchEntity.PoolId,
                coin = batchEntity.Coin,
                address = intent.Address,
                state = PayoutIntentStates.Reserved,
                amount = intent.Amount,
                balancesnapshotamount = intent.BalanceSnapshotAmount,
                balancesnapshotupdated = intent.BalanceSnapshotUpdated,
                paymentthreshold = intent.PaymentThreshold,
                created = batch.Created,
                updated = batch.Created
            }, tx, cancellationToken: ct));

            intentEntities.Add(intentEntity);
        }

        return MapBatch(batchEntity, intentEntities.Select(MapIntent).ToArray());
    }

    public async Task<PayoutSendAttempt> CreateSendAttemptAsync(IDbConnection con, IDbTransaction tx, CreatePayoutSendAttemptRequest attempt,
        IReadOnlyCollection<long> intentIds, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        if(attempt == null)
            throw new ArgumentNullException(nameof(attempt));

        var intentIdArray = intentIds?.ToArray() ?? Array.Empty<long>();
        if(intentIdArray.Length == 0)
            throw new ArgumentException("At least one payout intent id is required", nameof(intentIds));

        var distinctIntentIds = intentIdArray.Distinct().ToArray();
        if(distinctIntentIds.Length != intentIdArray.Length)
            throw new ArgumentException("Duplicate payout intent ids are not allowed", nameof(intentIds));

        const string loadBatchQuery = @"SELECT * FROM payout_batches
            WHERE id = @batchid AND poolid = @poolid AND coin = @coin
            FOR UPDATE";

        var batch = await con.QuerySingleOrDefaultAsync<Entities.PayoutBatch>(new CommandDefinition(loadBatchQuery, new
        {
            batchid = attempt.BatchId,
            poolid = attempt.PoolId,
            coin = attempt.Coin
        }, tx, cancellationToken: ct));

        if(batch == null)
            throw new InvalidOperationException("Payout send attempt parent batch does not exist for the requested batch/pool/coin");

        if(batch.State != PayoutBatchStates.Reserved)
            throw new InvalidOperationException("Payout send attempts can only be created while the parent batch is reserved");

        const string loadIntentsQuery = @"SELECT * FROM payout_intents
            WHERE batchid = @batchid AND poolid = @poolid AND coin = @coin
              AND state = @reserved AND id = ANY(@intentids)
            ORDER BY id
            FOR UPDATE";

        var intentEntities = (await con.QueryAsync<Entities.PayoutIntent>(new CommandDefinition(loadIntentsQuery, new
        {
            batchid = attempt.BatchId,
            poolid = attempt.PoolId,
            coin = attempt.Coin,
            reserved = PayoutIntentStates.Reserved,
            intentids = distinctIntentIds
        }, tx, cancellationToken: ct))).ToArray();

        if(intentEntities.Length != distinctIntentIds.Length)
            throw new InvalidOperationException("One or more payout intents do not belong to the requested batch/pool/coin or are not reserved");

        if(attempt.RecipientCount != intentEntities.Length)
            throw new InvalidOperationException("Payout send attempt recipient count does not match supplied intents");

        var amountSnapshot = intentEntities.Sum(x => x.Amount);
        if(attempt.AmountSnapshot != amountSnapshot)
            throw new InvalidOperationException("Payout send attempt amount snapshot does not match supplied intents");

        const string insertAttemptQuery = @"INSERT INTO payout_send_attempts(batchid, poolid, coin, attemptno, method, state,
                requesthash, requestsummary, recipientcount, amountsnapshot, created, updated)
            VALUES(@batchid, @poolid, @coin, @attemptno, @method, @state, @requesthash, @requestsummary,
                @recipientcount, @amountsnapshot, @created, @updated)
            RETURNING *";

        var attemptEntity = await con.QuerySingleAsync<Entities.PayoutSendAttempt>(new CommandDefinition(insertAttemptQuery, new
        {
            batchid = attempt.BatchId,
            poolid = attempt.PoolId,
            coin = attempt.Coin,
            attemptno = attempt.AttemptNo,
            method = attempt.Method,
            state = PayoutSendAttemptStates.Prepared,
            requesthash = attempt.RequestHash,
            requestsummary = attempt.RequestSummary,
            recipientcount = attempt.RecipientCount,
            amountsnapshot = attempt.AmountSnapshot,
            created = attempt.Created,
            updated = attempt.Created
        }, tx, cancellationToken: ct));

        const string insertAttemptIntentQuery = @"INSERT INTO payout_attempt_intents(attemptid, intentid, batchid, poolid,
                coin, state, amount, created, updated)
            VALUES(@attemptid, @intentid, @batchid, @poolid, @coin, @state, @amount, @created, @updated)
            RETURNING *";

        var attemptIntentEntities = new List<Entities.PayoutAttemptIntent>(intentEntities.Length);

        foreach(var intent in intentEntities)
        {
            var attemptIntentEntity = await con.QuerySingleAsync<Entities.PayoutAttemptIntent>(new CommandDefinition(insertAttemptIntentQuery, new
            {
                attemptid = attemptEntity.Id,
                intentid = intent.Id,
                batchid = attemptEntity.BatchId,
                poolid = attemptEntity.PoolId,
                coin = attemptEntity.Coin,
                state = PayoutAttemptIntentStates.Active,
                amount = intent.Amount,
                created = attempt.Created,
                updated = attempt.Created
            }, tx, cancellationToken: ct));

            attemptIntentEntities.Add(attemptIntentEntity);
        }

        return MapAttempt(attemptEntity, attemptIntentEntities.Select(MapAttemptIntent).ToArray());
    }

    public async Task<PayoutExternalConfirmation> InsertExternalConfirmationAsync(IDbConnection con, IDbTransaction tx,
        PayoutExternalConfirmation confirmation, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        if(confirmation == null)
            throw new ArgumentNullException(nameof(confirmation));

        ValidateEvidence(new PayoutAttemptEvidence
        {
            Kind = confirmation.Kind,
            Value = confirmation.Value
        });

        const string query = @"INSERT INTO payout_external_confirmations(poolid, coin, batchid, attemptid, intentid, kind, value, created)
            VALUES(@poolid, @coin, @batchid, @attemptid, @intentid, @kind, @value, @created)
            RETURNING *";

        var entity = await con.QuerySingleAsync<Entities.PayoutExternalConfirmation>(new CommandDefinition(query, new
        {
            poolid = confirmation.PoolId,
            coin = confirmation.Coin,
            batchid = confirmation.BatchId,
            attemptid = confirmation.AttemptId,
            intentid = confirmation.IntentId,
            kind = confirmation.Kind,
            value = confirmation.Value,
            created = confirmation.Created
        }, tx, cancellationToken: ct));

        return MapExternalConfirmation(entity);
    }

    public async Task<bool> MarkAttemptSendingAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId, DateTime updated, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        const string loadAttemptQuery = @"SELECT * FROM payout_send_attempts
            WHERE id = @attemptid AND poolid = @poolid AND state = @prepared
            FOR UPDATE";

        var attempt = await con.QuerySingleOrDefaultAsync<Entities.PayoutSendAttempt>(new CommandDefinition(loadAttemptQuery, new
        {
            attemptid = attemptId,
            poolid = poolId,
            prepared = PayoutSendAttemptStates.Prepared
        }, tx, cancellationToken: ct));

        if(attempt == null)
            return false;

        const string loadBatchQuery = @"SELECT * FROM payout_batches
            WHERE id = @batchid AND poolid = @poolid AND coin = @coin
            FOR UPDATE";

        var batch = await con.QuerySingleOrDefaultAsync<Entities.PayoutBatch>(new CommandDefinition(loadBatchQuery, new
        {
            batchid = attempt.BatchId,
            poolid = attempt.PoolId,
            coin = attempt.Coin
        }, tx, cancellationToken: ct));

        if(batch == null)
            return false;

        if(batch.State != PayoutBatchStates.Reserved && batch.State != PayoutBatchStates.Sending)
            return false;

        var activeMappingCount = await CountAttemptMappingsAsync(con, tx, attempt.Id, PayoutAttemptIntentStates.Active, ct);
        if(activeMappingCount == 0)
            throw new InvalidOperationException("Prepared payout send attempt has no active intent mappings");

        const string attemptQuery = @"UPDATE payout_send_attempts
            SET state = @sending, updated = @updated
            WHERE id = @attemptid AND poolid = @poolid AND state = @prepared";

        var updatedAttemptRows = await con.ExecuteAsync(new CommandDefinition(attemptQuery, new
        {
            attemptid = attempt.Id,
            poolid = poolId,
            prepared = PayoutSendAttemptStates.Prepared,
            sending = PayoutSendAttemptStates.Sending,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedAttemptRows, 1, "prepared payout attempt for sending transition");

        const string intentsQuery = @"UPDATE payout_intents pi
            SET state = @sending, updated = @updated
            FROM payout_attempt_intents pai
            WHERE pai.intentid = pi.id AND pai.attemptid = @attemptid
              AND pai.state = @active AND pi.state = @reserved";

        var updatedIntentRows = await con.ExecuteAsync(new CommandDefinition(intentsQuery, new
        {
            attemptid = attemptId,
            active = PayoutAttemptIntentStates.Active,
            reserved = PayoutIntentStates.Reserved,
            sending = PayoutIntentStates.Sending,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedIntentRows, activeMappingCount, "reserved payout intents for send attempt");

        if(batch.State == PayoutBatchStates.Reserved)
        {
            const string batchQuery = @"UPDATE payout_batches
                SET state = @sending, updated = @updated
                WHERE id = @batchid AND poolid = @poolid AND coin = @coin AND state = @reserved";

            var updatedBatchRows = await con.ExecuteAsync(new CommandDefinition(batchQuery, new
            {
                batchid = batch.Id,
                poolid = batch.PoolId,
                coin = batch.Coin,
                reserved = PayoutBatchStates.Reserved,
                sending = PayoutBatchStates.Sending,
                updated
            }, tx, cancellationToken: ct));

            EnsureRowCount(updatedBatchRows, 1, "reserved payout batch for first send attempt");
        }

        return true;
    }

    public async Task<bool> MarkAttemptAcceptedAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId,
        PayoutAttemptEvidence evidence, DateTime updated, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        ValidateEvidence(evidence);

        const string loadAttemptQuery = @"SELECT * FROM payout_send_attempts
            WHERE id = @attemptid AND poolid = @poolid AND state = @sending
            FOR UPDATE";

        var attempt = await con.QuerySingleOrDefaultAsync<Entities.PayoutSendAttempt>(new CommandDefinition(loadAttemptQuery, new
        {
            attemptid = attemptId,
            poolid = poolId,
            sending = PayoutSendAttemptStates.Sending
        }, tx, cancellationToken: ct));

        if(attempt == null)
            return false;

        const string loadBatchQuery = @"SELECT * FROM payout_batches
            WHERE id = @batchid AND poolid = @poolid AND coin = @coin AND state = @sending
            FOR UPDATE";

        var batch = await con.QuerySingleOrDefaultAsync<Entities.PayoutBatch>(new CommandDefinition(loadBatchQuery, new
        {
            batchid = attempt.BatchId,
            poolid = attempt.PoolId,
            coin = attempt.Coin,
            sending = PayoutBatchStates.Sending
        }, tx, cancellationToken: ct));

        if(batch == null)
            return false;

        var activeMappingCount = await CountAttemptMappingsAsync(con, tx, attempt.Id, PayoutAttemptIntentStates.Active, ct);
        if(activeMappingCount == 0)
            throw new InvalidOperationException("Sending payout attempt has no active intent mappings");

        var sendingIntentCount = await CountAttemptMappingsWithIntentStateAsync(con, tx, attempt.Id,
            PayoutAttemptIntentStates.Active, PayoutIntentStates.Sending, ct);
        EnsureRowCount(sendingIntentCount, activeMappingCount, "sending payout intents for accepted attempt");

        var isOperationId = evidence.Kind == PayoutExternalConfirmationKinds.OperationId;
        var transactionConfirmationData = isOperationId ? null : evidence.Value;
        var externalOperationId = isOperationId ? evidence.Value : null;

        await InsertExternalConfirmationAsync(con, tx, new PayoutExternalConfirmation
        {
            PoolId = attempt.PoolId,
            Coin = attempt.Coin,
            BatchId = attempt.BatchId,
            AttemptId = attempt.Id,
            Kind = evidence.Kind,
            Value = evidence.Value,
            Created = updated
        }, ct);

        const string attemptQuery = @"UPDATE payout_send_attempts
            SET state = @accepted, transactionconfirmationdata = @transactionconfirmationdata,
                externaloperationid = @externaloperationid, updated = @updated, completed = @updated
            WHERE id = @attemptid AND state = @sending";

        var updatedAttemptRows = await con.ExecuteAsync(new CommandDefinition(attemptQuery, new
        {
            attemptid = attempt.Id,
            accepted = PayoutSendAttemptStates.Accepted,
            sending = PayoutSendAttemptStates.Sending,
            transactionconfirmationdata = transactionConfirmationData,
            externaloperationid = externalOperationId,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedAttemptRows, 1, "sending payout attempt");

        const string intentsQuery = @"UPDATE payout_intents pi
            SET state = @submitted, transactionconfirmationdata = COALESCE(@transactionconfirmationdata, pi.transactionconfirmationdata),
                updated = @updated
            FROM payout_attempt_intents pai
            WHERE pai.intentid = pi.id AND pai.attemptid = @attemptid
              AND pai.state = @active AND pi.state = @sending";

        var updatedIntentRows = await con.ExecuteAsync(new CommandDefinition(intentsQuery, new
        {
            attemptid = attempt.Id,
            active = PayoutAttemptIntentStates.Active,
            submitted = PayoutIntentStates.Submitted,
            sending = PayoutIntentStates.Sending,
            transactionconfirmationdata = transactionConfirmationData,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedIntentRows, activeMappingCount, "sending payout intents for accepted attempt");

        const string mappingsQuery = @"UPDATE payout_attempt_intents
            SET state = @accepted, transactionconfirmationdata = @transactionconfirmationdata, updated = @updated
            WHERE attemptid = @attemptid AND state = @active";

        var updatedMappingRows = await con.ExecuteAsync(new CommandDefinition(mappingsQuery, new
        {
            attemptid = attempt.Id,
            accepted = PayoutAttemptIntentStates.Accepted,
            active = PayoutAttemptIntentStates.Active,
            transactionconfirmationdata = transactionConfirmationData,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedMappingRows, activeMappingCount, "active payout attempt-intent mappings for accepted attempt");

        var blockingIntentCount = await CountBatchIntentsInStatesAsync(con, tx, attempt.BatchId, new[]
        {
            PayoutIntentStates.Reserved,
            PayoutIntentStates.Sending
        }, ct);

        if(blockingIntentCount == 0)
        {
            const string batchQuery = @"UPDATE payout_batches
                SET state = @submitted, transactionconfirmationdata = COALESCE(@transactionconfirmationdata, transactionconfirmationdata),
                    externaloperationid = COALESCE(@externaloperationid, externaloperationid), submitted = @updated, updated = @updated
                WHERE id = @batchid AND poolid = @poolid AND coin = @coin AND state = @sending";

            var updatedBatchRows = await con.ExecuteAsync(new CommandDefinition(batchQuery, new
            {
                batchid = batch.Id,
                poolid = batch.PoolId,
                coin = batch.Coin,
                submitted = PayoutBatchStates.Submitted,
                sending = PayoutBatchStates.Sending,
                transactionconfirmationdata = transactionConfirmationData,
                externaloperationid = externalOperationId,
                updated
            }, tx, cancellationToken: ct));

            EnsureRowCount(updatedBatchRows, 1, "sending payout batch for final accepted attempt");
        }

        return true;
    }

    public Task<bool> MarkAttemptAmbiguousAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId,
        string errorCode, string errorMessage, DateTime updated, CancellationToken ct)
    {
        ValidateError(errorCode, errorMessage);
        return MarkSendingAttemptAmbiguousAsync(con, tx, attemptId, poolId, errorCode, errorMessage, updated, ct);
    }

    public async Task<bool> MarkAttemptFailedPreAcceptAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId,
        string errorCode, string errorMessage, DateTime updated, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);
        ValidateError(errorCode, errorMessage);

        const string loadAttemptQuery = @"SELECT * FROM payout_send_attempts
            WHERE id = @attemptid AND poolid = @poolid AND state = ANY(@expectedstates)
            FOR UPDATE";

        var attempt = await con.QuerySingleOrDefaultAsync<Entities.PayoutSendAttempt>(new CommandDefinition(loadAttemptQuery, new
        {
            attemptid = attemptId,
            poolid = poolId,
            expectedstates = new[] { PayoutSendAttemptStates.Prepared, PayoutSendAttemptStates.Sending }
        }, tx, cancellationToken: ct));

        if(attempt == null)
            return false;

        var expectedIntentState = attempt.State == PayoutSendAttemptStates.Prepared
            ? PayoutIntentStates.Reserved
            : PayoutIntentStates.Sending;

        var activeMappingCount = await CountAttemptMappingsAsync(con, tx, attempt.Id, PayoutAttemptIntentStates.Active, ct);
        if(activeMappingCount == 0)
            throw new InvalidOperationException("Payout attempt has no active intent mappings");

        var expectedIntentCount = await CountAttemptMappingsWithIntentStateAsync(con, tx, attempt.Id,
            PayoutAttemptIntentStates.Active, expectedIntentState, ct);
        EnsureRowCount(expectedIntentCount, activeMappingCount, "expected-state payout intents for failed pre-accept attempt");

        const string attemptQuery = @"UPDATE payout_send_attempts
            SET state = @failed, errorcode = @errorcode, errormessage = @errormessage,
                updated = @updated, completed = @updated
            WHERE id = @attemptid AND poolid = @poolid AND state = @expectedstate";

        var updatedAttemptRows = await con.ExecuteAsync(new CommandDefinition(attemptQuery, new
        {
            attemptid = attempt.Id,
            poolid = poolId,
            expectedstate = attempt.State,
            failed = PayoutSendAttemptStates.FailedPreAccept,
            errorcode = errorCode,
            errormessage = errorMessage,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedAttemptRows, 1, "prepared/sending payout attempt for failed pre-accept transition");

        await MarkAttemptMappingsAndIntentsAsync(con, tx, attempt.Id, PayoutAttemptIntentStates.Active,
            PayoutAttemptIntentStates.FailedPreAccept, expectedIntentState, PayoutIntentStates.Reserved,
            errorCode, errorMessage, activeMappingCount, updated, ct);

        await MarkBatchReservedIfNoBlockingAttemptsAsync(con, tx, attempt.BatchId, updated, ct);
        return true;
    }

    public async Task<bool> MarkAttemptFailedNoAcceptAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId,
        string errorCode, string errorMessage, DateTime updated, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);
        ValidateError(errorCode, errorMessage);

        const string loadAttemptQuery = @"SELECT * FROM payout_send_attempts
            WHERE id = @attemptid AND poolid = @poolid AND state = @ambiguous
            FOR UPDATE";

        var attempt = await con.QuerySingleOrDefaultAsync<Entities.PayoutSendAttempt>(new CommandDefinition(loadAttemptQuery, new
        {
            attemptid = attemptId,
            poolid = poolId,
            ambiguous = PayoutSendAttemptStates.AmbiguousRequiresReview
        }, tx, cancellationToken: ct));

        if(attempt == null)
            return false;

        var ambiguousMappingCount = await CountAttemptMappingsAsync(con, tx, attempt.Id,
            PayoutAttemptIntentStates.AmbiguousRequiresReview, ct);
        if(ambiguousMappingCount == 0)
            throw new InvalidOperationException("Ambiguous payout attempt has no ambiguous intent mappings");

        var ambiguousIntentCount = await CountAttemptMappingsWithIntentStateAsync(con, tx, attempt.Id,
            PayoutAttemptIntentStates.AmbiguousRequiresReview, PayoutIntentStates.AmbiguousRequiresReview, ct);
        EnsureRowCount(ambiguousIntentCount, ambiguousMappingCount, "ambiguous payout intents for failed no-accept transition");

        const string attemptQuery = @"UPDATE payout_send_attempts
            SET state = @failed, errorcode = @errorcode, errormessage = @errormessage,
                updated = @updated, completed = @updated
            WHERE id = @attemptid AND poolid = @poolid AND state = @ambiguous
            RETURNING *";

        var updatedAttempt = await con.QuerySingleOrDefaultAsync<Entities.PayoutSendAttempt>(new CommandDefinition(attemptQuery, new
        {
            attemptid = attempt.Id,
            poolid = poolId,
            ambiguous = PayoutSendAttemptStates.AmbiguousRequiresReview,
            failed = PayoutSendAttemptStates.FailedNoAccept,
            errorcode = errorCode,
            errormessage = errorMessage,
            updated
        }, tx, cancellationToken: ct));

        if(updatedAttempt == null)
            throw new InvalidOperationException("Ambiguous payout attempt disappeared during failed no-accept transition");

        await MarkAttemptMappingsAndIntentsAsync(con, tx, attempt.Id,
            PayoutAttemptIntentStates.AmbiguousRequiresReview, PayoutAttemptIntentStates.FailedNoAccept,
            PayoutIntentStates.AmbiguousRequiresReview, PayoutIntentStates.Reserved, errorCode, errorMessage,
            ambiguousMappingCount, updated, ct);

        await MarkBatchReservedIfNoBlockingAttemptsAsync(con, tx, attempt.BatchId, updated, ct);
        return true;
    }

    public async Task<bool> MarkBatchCancelledAsync(IDbConnection con, IDbTransaction tx, long batchId, string poolId,
        string errorCode, string errorMessage, DateTime updated, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);
        ValidateError(errorCode, errorMessage);

        const string loadBatchQuery = @"SELECT * FROM payout_batches
            WHERE id = @batchid AND poolid = @poolid AND state = @reserved
            FOR UPDATE";

        var batch = await con.QuerySingleOrDefaultAsync<Entities.PayoutBatch>(new CommandDefinition(loadBatchQuery, new
        {
            batchid = batchId,
            poolid = poolId,
            reserved = PayoutBatchStates.Reserved
        }, tx, cancellationToken: ct));

        if(batch == null)
            return false;

        var attemptCount = await CountBatchAttemptsAsync(con, tx, batch.Id, ct);
        if(attemptCount > 0)
            return false;

        var reservedIntentCount = await CountBatchIntentsAsync(con, tx, batch.Id, PayoutIntentStates.Reserved, ct);
        if(reservedIntentCount == 0)
            throw new InvalidOperationException("Reserved payout batch has no reserved intents to cancel");

        const string intentsQuery = @"UPDATE payout_intents
            SET state = @cancelled, errorcode = @errorcode, errormessage = @errormessage, updated = @updated
            WHERE batchid = @batchid AND poolid = @poolid AND state = @reserved";

        var updatedIntentRows = await con.ExecuteAsync(new CommandDefinition(intentsQuery, new
        {
            batchid = batch.Id,
            poolid = poolId,
            reserved = PayoutIntentStates.Reserved,
            cancelled = PayoutIntentStates.Cancelled,
            errorcode = errorCode,
            errormessage = errorMessage,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedIntentRows, reservedIntentCount, "reserved payout intents for batch cancellation");

        const string batchQuery = @"UPDATE payout_batches
            SET state = @cancelled, errorcode = @errorcode, errormessage = @errormessage, updated = @updated
            WHERE id = @batchid AND poolid = @poolid AND state = @reserved
              AND NOT EXISTS (
                  SELECT 1 FROM payout_send_attempts
                  WHERE batchid = @batchid
              )";

        var updatedBatchRows = await con.ExecuteAsync(new CommandDefinition(batchQuery, new
        {
            batchid = batch.Id,
            poolid = poolId,
            reserved = PayoutBatchStates.Reserved,
            cancelled = PayoutBatchStates.Cancelled,
            errorcode = errorCode,
            errormessage = errorMessage,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedBatchRows, 1, "reserved payout batch for cancellation");

        var remainingAttemptCount = await CountBatchAttemptsAsync(con, tx, batch.Id, ct);
        EnsureRowCount(remainingAttemptCount, 0, "payout send attempts after batch cancellation");

        var remainingActiveMappingCount = await CountBatchAttemptMappingsAsync(con, tx, batch.Id,
            PayoutAttemptIntentStates.Active, ct);
        EnsureRowCount(remainingActiveMappingCount, 0, "active payout attempt-intent mappings after batch cancellation");

        return true;
    }

    public async Task<PayoutBatch> GetActiveBatchForPoolAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        con = RequireConnection(con);

        const string query = @"SELECT * FROM payout_batches
            WHERE poolid = @poolid AND state = ANY(@states)
            ORDER BY created DESC
            FETCH NEXT 1 ROWS ONLY";

        var batch = await con.QuerySingleOrDefaultAsync<Entities.PayoutBatch>(new CommandDefinition(query, new
        {
            poolid = poolId,
            states = ActiveBatchStates
        }, cancellationToken: ct));

        if(batch == null)
            return null;

        var intents = await LoadIntentsForBatchAsync(con, null, batch.Id, ct);
        return MapBatch(batch, intents.Select(MapIntent).ToArray());
    }

    public async Task<PayoutBatch> GetActiveBatchForPoolAsync(IDbConnection con, IDbTransaction tx, string poolId, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        const string query = @"SELECT * FROM payout_batches
            WHERE poolid = @poolid AND state = ANY(@states)
            ORDER BY created DESC
            LIMIT 1
            FOR UPDATE";

        var batch = await con.QuerySingleOrDefaultAsync<Entities.PayoutBatch>(new CommandDefinition(query, new
        {
            poolid = poolId,
            states = ActiveBatchStates
        }, tx, cancellationToken: ct));

        if(batch == null)
            return null;

        var intents = await LoadIntentsForBatchAsync(con, tx, batch.Id, ct);
        return MapBatch(batch, intents.Select(MapIntent).ToArray());
    }

    public async Task<PayoutBatch> GetBatchForUpdateAsync(IDbConnection con, IDbTransaction tx, long batchId, string poolId,
        string coin, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        const string query = @"SELECT * FROM payout_batches
            WHERE id = @batchid AND poolid = @poolid AND coin = @coin
            FOR UPDATE";

        var batch = await con.QuerySingleOrDefaultAsync<Entities.PayoutBatch>(new CommandDefinition(query, new
        {
            batchid = batchId,
            poolid = poolId,
            coin
        }, tx, cancellationToken: ct));

        return batch == null ? null : MapBatch(batch);
    }

    public async Task<PayoutIntent[]> GetReservedIntentsForBatchAsync(IDbConnection con, IDbTransaction tx, long batchId,
        string poolId, string coin, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        const string query = @"SELECT * FROM payout_intents
            WHERE batchid = @batchid AND poolid = @poolid AND coin = @coin AND state = @reserved
            ORDER BY address, id
            FOR UPDATE";

        return (await con.QueryAsync<Entities.PayoutIntent>(new CommandDefinition(query, new
            {
                batchid = batchId,
                poolid = poolId,
                coin,
                reserved = PayoutIntentStates.Reserved
            }, tx, cancellationToken: ct)))
            .Select(MapIntent)
            .ToArray();
    }

    public async Task<long> GetSendAttemptCountForBatchAsync(IDbConnection con, IDbTransaction tx, long batchId, string poolId,
        string coin, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        const string query = @"SELECT COUNT(*) FROM payout_send_attempts
            WHERE batchid = @batchid AND poolid = @poolid AND coin = @coin";

        return await con.QuerySingleAsync<long>(new CommandDefinition(query, new
        {
            batchid = batchId,
            poolid = poolId,
            coin
        }, tx, cancellationToken: ct));
    }

    public async Task<PayoutBatch[]> GetRecoverableBatchesAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        con = RequireConnection(con);

        const string query = @"SELECT * FROM payout_batches
            WHERE poolid = @poolid AND state = ANY(@states)
            ORDER BY created";

        return (await con.QueryAsync<Entities.PayoutBatch>(new CommandDefinition(query, new
            {
                poolid = poolId,
                states = RecoverableBatchStates
            }, cancellationToken: ct)))
            .Select(x => MapBatch(x))
            .ToArray();
    }

    public async Task<PayoutSendAttempt[]> GetStaleSendingAttemptsAsync(IDbConnection con, DateTime before, int limit, CancellationToken ct)
    {
        con = RequireConnection(con);

        const string query = @"SELECT * FROM payout_send_attempts
            WHERE state = @sending AND updated < @before
            ORDER BY updated
            LIMIT @limit";

        return (await con.QueryAsync<Entities.PayoutSendAttempt>(new CommandDefinition(query, new
            {
                sending = PayoutSendAttemptStates.Sending,
                before,
                limit
            }, cancellationToken: ct)))
            .Select(x => MapAttempt(x))
            .ToArray();
    }

    public async Task<PayoutBalanceProjection[]> GetBalanceProjectionsAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        con = RequireConnection(con);

        const string query = @"SELECT
                b.poolid AS PoolId,
                b.address AS Address,
                b.amount AS Total,
                COALESCE(SUM(CASE WHEN pi.state = ANY(@reservedstates) THEN pi.amount ELSE 0 END), 0) AS Reserved,
                COALESCE(SUM(CASE WHEN pi.state = @ambiguousstate THEN pi.amount ELSE 0 END), 0) AS Ambiguous,
                b.amount - COALESCE(SUM(CASE WHEN pi.state = ANY(@activestates) THEN pi.amount ELSE 0 END), 0) AS Available
            FROM balances b
            LEFT JOIN payout_intents pi ON pi.poolid = b.poolid
                AND pi.address = b.address
                AND pi.state = ANY(@activestates)
            WHERE b.poolid = @poolid
            GROUP BY b.poolid, b.address, b.amount
            ORDER BY b.address";

        return (await con.QueryAsync<PayoutBalanceProjection>(new CommandDefinition(query, new
            {
                poolid = poolId,
                reservedstates = ReservedAmountStates,
                ambiguousstate = PayoutIntentStates.AmbiguousRequiresReview,
                activestates = ActiveIntentStates
            }, cancellationToken: ct)))
            .ToArray();
    }

    private async Task<bool> MarkSendingAttemptAmbiguousAsync(IDbConnection con, IDbTransaction tx, long attemptId, string poolId,
        string errorCode, string errorMessage, DateTime updated, CancellationToken ct)
    {
        con = RequireConnection(con);
        tx = RequireTransaction(tx);

        const string loadAttemptQuery = @"SELECT * FROM payout_send_attempts
            WHERE id = @attemptid AND poolid = @poolid AND state = @sending
            FOR UPDATE";

        var attempt = await con.QuerySingleOrDefaultAsync<Entities.PayoutSendAttempt>(new CommandDefinition(loadAttemptQuery, new
        {
            attemptid = attemptId,
            poolid = poolId,
            sending = PayoutSendAttemptStates.Sending
        }, tx, cancellationToken: ct));

        if(attempt == null)
            return false;

        var activeMappingCount = await CountAttemptMappingsAsync(con, tx, attempt.Id, PayoutAttemptIntentStates.Active, ct);
        if(activeMappingCount == 0)
            throw new InvalidOperationException("Sending payout attempt has no active intent mappings");

        var sendingIntentCount = await CountAttemptMappingsWithIntentStateAsync(con, tx, attempt.Id,
            PayoutAttemptIntentStates.Active, PayoutIntentStates.Sending, ct);
        EnsureRowCount(sendingIntentCount, activeMappingCount, "sending payout intents for ambiguous transition");

        const string attemptQuery = @"UPDATE payout_send_attempts
            SET state = @newstate, errorcode = @errorcode, errormessage = @errormessage,
                updated = @updated, completed = @updated
            WHERE id = @attemptid AND poolid = @poolid AND state = @sending";

        var updatedAttemptRows = await con.ExecuteAsync(new CommandDefinition(attemptQuery, new
        {
            attemptid = attempt.Id,
            poolid = poolId,
            sending = PayoutSendAttemptStates.Sending,
            newstate = PayoutSendAttemptStates.AmbiguousRequiresReview,
            errorcode = errorCode,
            errormessage = errorMessage,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedAttemptRows, 1, "sending payout attempt for ambiguous transition");

        await MarkAttemptMappingsAndIntentsAsync(con, tx, attempt.Id, PayoutAttemptIntentStates.Active,
            PayoutAttemptIntentStates.AmbiguousRequiresReview, PayoutIntentStates.Sending,
            PayoutIntentStates.AmbiguousRequiresReview, errorCode, errorMessage, activeMappingCount, updated, ct);

        const string batchQuery = @"UPDATE payout_batches
            SET state = @ambiguous, errorcode = @errorcode, errormessage = @errormessage, updated = @updated
            WHERE id = @batchid AND state = @sending";

        var updatedBatchRows = await con.ExecuteAsync(new CommandDefinition(batchQuery, new
        {
            batchid = attempt.BatchId,
            sending = PayoutBatchStates.Sending,
            ambiguous = PayoutBatchStates.AmbiguousRequiresReview,
            errorcode = errorCode,
            errormessage = errorMessage,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedBatchRows, 1, "sending payout batch for ambiguous transition");

        return true;
    }

    private static async Task MarkAttemptMappingsAndIntentsAsync(IDbConnection con, IDbTransaction tx, long attemptId,
        string expectedMappingState, string newMappingState, string expectedIntentState, string newIntentState,
        string errorCode, string errorMessage, long expectedRows, DateTime updated, CancellationToken ct)
    {
        const string intentsQuery = @"UPDATE payout_intents pi
            SET state = @newintentstate, errorcode = @errorcode, errormessage = @errormessage, updated = @updated
            FROM payout_attempt_intents pai
            WHERE pai.intentid = pi.id AND pai.attemptid = @attemptid
              AND pai.state = @expectedmappingstate AND pi.state = @expectedintentstate";

        var updatedIntentRows = await con.ExecuteAsync(new CommandDefinition(intentsQuery, new
        {
            attemptid = attemptId,
            expectedmappingstate = expectedMappingState,
            expectedintentstate = expectedIntentState,
            newintentstate = newIntentState,
            errorcode = errorCode,
            errormessage = errorMessage,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedIntentRows, expectedRows, "expected-state payout intents for attempt transition");

        const string mappingsQuery = @"UPDATE payout_attempt_intents
            SET state = @newmappingstate, updated = @updated
            WHERE attemptid = @attemptid AND state = @expectedmappingstate";

        var updatedMappingRows = await con.ExecuteAsync(new CommandDefinition(mappingsQuery, new
        {
            attemptid = attemptId,
            expectedmappingstate = expectedMappingState,
            newmappingstate = newMappingState,
            updated
        }, tx, cancellationToken: ct));

        EnsureRowCount(updatedMappingRows, expectedRows, "expected-state payout attempt-intent mappings");
    }

    private static async Task MarkBatchReservedIfNoBlockingAttemptsAsync(IDbConnection con, IDbTransaction tx, long batchId,
        DateTime updated, CancellationToken ct)
    {
        const string query = @"UPDATE payout_batches
            SET state = @reserved, updated = @updated
            WHERE id = @batchid AND state = ANY(@returnablestates)
              AND NOT EXISTS (
                  SELECT 1 FROM payout_send_attempts
                  WHERE batchid = @batchid AND state = ANY(@blockingstates)
              )";

        await con.ExecuteAsync(new CommandDefinition(query, new
        {
            batchid = batchId,
            reserved = PayoutBatchStates.Reserved,
            returnablestates = new[]
            {
                PayoutBatchStates.Reserved,
                PayoutBatchStates.Sending,
                PayoutBatchStates.AmbiguousRequiresReview
            },
            blockingstates = new[]
            {
                PayoutSendAttemptStates.Sending,
                PayoutSendAttemptStates.Accepted,
                PayoutSendAttemptStates.AmbiguousRequiresReview
            },
            updated
        }, tx, cancellationToken: ct));
    }

    private static async Task<long> CountAttemptMappingsAsync(IDbConnection con, IDbTransaction tx, long attemptId,
        string mappingState, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM payout_attempt_intents
            WHERE attemptid = @attemptid AND state = @mappingstate";

        return await con.QuerySingleAsync<long>(new CommandDefinition(query, new
        {
            attemptid = attemptId,
            mappingstate = mappingState
        }, tx, cancellationToken: ct));
    }

    private static async Task<long> CountAttemptMappingsWithIntentStateAsync(IDbConnection con, IDbTransaction tx, long attemptId,
        string mappingState, string intentState, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*)
            FROM payout_attempt_intents pai
            JOIN payout_intents pi ON pi.id = pai.intentid
            WHERE pai.attemptid = @attemptid AND pai.state = @mappingstate AND pi.state = @intentstate";

        return await con.QuerySingleAsync<long>(new CommandDefinition(query, new
        {
            attemptid = attemptId,
            mappingstate = mappingState,
            intentstate = intentState
        }, tx, cancellationToken: ct));
    }

    private static async Task<long> CountBatchIntentsAsync(IDbConnection con, IDbTransaction tx, long batchId,
        string intentState, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM payout_intents
            WHERE batchid = @batchid AND state = @intentstate";

        return await con.QuerySingleAsync<long>(new CommandDefinition(query, new
        {
            batchid = batchId,
            intentstate = intentState
        }, tx, cancellationToken: ct));
    }

    private static async Task<long> CountBatchIntentsInStatesAsync(IDbConnection con, IDbTransaction tx, long batchId,
        string[] intentStates, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM payout_intents
            WHERE batchid = @batchid AND state = ANY(@intentstates)";

        return await con.QuerySingleAsync<long>(new CommandDefinition(query, new
        {
            batchid = batchId,
            intentstates = intentStates
        }, tx, cancellationToken: ct));
    }

    private static async Task<long> CountBatchAttemptsAsync(IDbConnection con, IDbTransaction tx, long batchId, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM payout_send_attempts
            WHERE batchid = @batchid";

        return await con.QuerySingleAsync<long>(new CommandDefinition(query, new
        {
            batchid = batchId
        }, tx, cancellationToken: ct));
    }

    private static async Task<long> CountBatchAttemptMappingsAsync(IDbConnection con, IDbTransaction tx, long batchId,
        string mappingState, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM payout_attempt_intents
            WHERE batchid = @batchid AND state = @mappingstate";

        return await con.QuerySingleAsync<long>(new CommandDefinition(query, new
        {
            batchid = batchId,
            mappingstate = mappingState
        }, tx, cancellationToken: ct));
    }

    private static IDbConnection RequireConnection(IDbConnection con)
    {
        return con ?? throw new ArgumentNullException(nameof(con));
    }

    private static IDbTransaction RequireTransaction(IDbTransaction tx)
    {
        return tx ?? throw new ArgumentNullException(nameof(tx));
    }

    private static void EnsureRowCount(long actualRows, long expectedRows, string target)
    {
        if(actualRows != expectedRows)
            throw new InvalidOperationException($"Expected to update {expectedRows} {target}, but updated {actualRows}");
    }

    private static void ValidateError(string errorCode, string errorMessage)
    {
        if(string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("Payout state transition requires a non-empty error code", nameof(errorCode));

        if(errorMessage != null && string.IsNullOrWhiteSpace(errorMessage))
            throw new ArgumentException("Payout state transition error message cannot be whitespace", nameof(errorMessage));
    }

    private static async Task<Entities.PayoutIntent[]> LoadIntentsForBatchAsync(IDbConnection con, IDbTransaction tx, long batchId,
        CancellationToken ct)
    {
        const string query = @"SELECT * FROM payout_intents WHERE batchid = @batchid ORDER BY id";

        return (await con.QueryAsync<Entities.PayoutIntent>(new CommandDefinition(query, new { batchid = batchId }, tx,
            cancellationToken: ct))).ToArray();
    }

    private static void ValidateEvidence(PayoutAttemptEvidence evidence)
    {
        if(evidence == null || string.IsNullOrWhiteSpace(evidence.Kind) || string.IsNullOrWhiteSpace(evidence.Value))
            throw new ArgumentException("Accepted payout attempt evidence requires kind and value", nameof(evidence));

        switch(evidence.Kind)
        {
            case PayoutExternalConfirmationKinds.TxId:
            case PayoutExternalConfirmationKinds.OperationId:
            case PayoutExternalConfirmationKinds.WalletAck:
            case PayoutExternalConfirmationKinds.RawHash:
                return;

            default:
                throw new ArgumentException($"Unsupported payout external confirmation kind '{evidence.Kind}'", nameof(evidence));
        }
    }

    private static PayoutBatch MapBatch(Entities.PayoutBatch entity, PayoutIntent[] intents = null)
    {
        return new PayoutBatch
        {
            Id = entity.Id,
            PoolId = entity.PoolId,
            Coin = entity.Coin,
            CoinFamily = entity.CoinFamily,
            Handler = entity.Handler,
            State = entity.State,
            SendShape = entity.SendShape,
            RecipientSetHash = entity.RecipientSetHash,
            MinimumAmount = entity.MinimumAmount,
            ReservedAmountSnapshot = entity.ReservedAmountSnapshot,
            IntentCountSnapshot = entity.IntentCountSnapshot,
            ExternalOperationId = entity.ExternalOperationId,
            TransactionConfirmationData = entity.TransactionConfirmationData,
            ErrorCode = entity.ErrorCode,
            ErrorMessage = entity.ErrorMessage,
            Created = entity.Created,
            Updated = entity.Updated,
            Submitted = entity.Submitted,
            Settled = entity.Settled,
            Reviewed = entity.Reviewed,
            Intents = intents ?? Array.Empty<PayoutIntent>()
        };
    }

    private static PayoutIntent MapIntent(Entities.PayoutIntent entity)
    {
        return new PayoutIntent
        {
            Id = entity.Id,
            BatchId = entity.BatchId,
            PoolId = entity.PoolId,
            Coin = entity.Coin,
            Address = entity.Address,
            State = entity.State,
            Amount = entity.Amount,
            BalanceSnapshotAmount = entity.BalanceSnapshotAmount,
            BalanceSnapshotUpdated = entity.BalanceSnapshotUpdated,
            PaymentThreshold = entity.PaymentThreshold,
            TransactionConfirmationData = entity.TransactionConfirmationData,
            PaymentId = entity.PaymentId,
            BalanceChangeId = entity.BalanceChangeId,
            Created = entity.Created,
            Updated = entity.Updated,
            Settled = entity.Settled,
            ErrorCode = entity.ErrorCode,
            ErrorMessage = entity.ErrorMessage
        };
    }

    private static PayoutSendAttempt MapAttempt(Entities.PayoutSendAttempt entity, PayoutAttemptIntent[] attemptIntents = null)
    {
        return new PayoutSendAttempt
        {
            Id = entity.Id,
            BatchId = entity.BatchId,
            PoolId = entity.PoolId,
            Coin = entity.Coin,
            AttemptNo = entity.AttemptNo,
            Method = entity.Method,
            State = entity.State,
            RequestHash = entity.RequestHash,
            RequestSummary = entity.RequestSummary,
            RecipientCount = entity.RecipientCount,
            AmountSnapshot = entity.AmountSnapshot,
            ExternalOperationId = entity.ExternalOperationId,
            TransactionConfirmationData = entity.TransactionConfirmationData,
            ErrorCode = entity.ErrorCode,
            ErrorMessage = entity.ErrorMessage,
            Created = entity.Created,
            Updated = entity.Updated,
            Completed = entity.Completed,
            AttemptIntents = attemptIntents ?? Array.Empty<PayoutAttemptIntent>()
        };
    }

    private static PayoutAttemptIntent MapAttemptIntent(Entities.PayoutAttemptIntent entity)
    {
        return new PayoutAttemptIntent
        {
            AttemptId = entity.AttemptId,
            IntentId = entity.IntentId,
            BatchId = entity.BatchId,
            PoolId = entity.PoolId,
            Coin = entity.Coin,
            State = entity.State,
            Amount = entity.Amount,
            TransactionConfirmationData = entity.TransactionConfirmationData,
            Created = entity.Created,
            Updated = entity.Updated
        };
    }

    private static PayoutExternalConfirmation MapExternalConfirmation(Entities.PayoutExternalConfirmation entity)
    {
        return new PayoutExternalConfirmation
        {
            Id = entity.Id,
            PoolId = entity.PoolId,
            Coin = entity.Coin,
            BatchId = entity.BatchId,
            AttemptId = entity.AttemptId,
            IntentId = entity.IntentId,
            Kind = entity.Kind,
            Value = entity.Value,
            Created = entity.Created
        };
    }
}
