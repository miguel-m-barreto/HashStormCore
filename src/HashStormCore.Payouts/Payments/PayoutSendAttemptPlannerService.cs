using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using HashStormCore.Payouts.Profiles;
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

    // Shared by CryptoNote block-based Base58 and standard Bitcoin-style Base58 (Alephium).
    private const string CryptoNoteBase58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    // Alephium LockupScript type bytes (see upstream protocol/script/LockupScript.scala).
    private const byte AlephiumTypeP2PKH = 0;
    private const byte AlephiumTypeP2MPKH = 1;
    private const byte AlephiumTypeP2SH = 2;
    private const byte AlephiumTypeP2C = 3;
    private const byte AlephiumTypeP2PK = 4;
    private const byte AlephiumTypeP2HMPK = 5;
    private const int AlephiumHashLength = 32;
    private const int AlephiumLockupScriptSerializedLength = 1 + AlephiumHashLength;
    private const int AlephiumGroupedKeySerializedLength = 39;
    private static readonly int[] CryptoNoteEncodedBlockSizes = { 0, 2, 3, 5, 6, 7, 9, 10, 11 };
    private const int CryptoNoteStandardPayloadLength = 32 + 32 + 4;
    private const int CryptoNoteIntegratedPayloadLength = 8 + 32 + 32 + 4;
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
                return new List<PayoutIntent[]> { orderedIntents };

            case PayoutSendShapes.AsyncOperation:
                return CreateChunkedGroups(orderedIntents, request.MaxRecipientsPerAttempt);

            case PayoutSendShapes.PerAddress:
                return orderedIntents.Select(x => new[] { x }).ToList();

            case PayoutSendShapes.AddressGroup:
                if(IsPaymentIdAwarePlanningPolicy(request.AttemptPlanningPolicy))
                    return CreatePaymentIdAwareGroups(request, orderedIntents);

                if(IsAlephiumGroupAwarePolicy(request.AttemptPlanningPolicy))
                    return CreateAlephiumGroupAwareGroups(request, orderedIntents);

                return orderedIntents
                    .Select((intent, index) => new { intent, index })
                    .GroupBy(x => x.index / request.MaxRecipientsPerAttempt)
                    .Select(x => x.Select(y => y.intent).ToArray())
                    .ToList();

            default:
                throw new ArgumentException($"Unsupported payout send shape '{request.SendShape}'", nameof(request));
        }
    }

    private static List<PayoutIntent[]> CreateChunkedGroups(PayoutIntent[] orderedIntents, int maxRecipientsPerAttempt)
    {
        return orderedIntents
            .Select((intent, index) => new { intent, index })
            .GroupBy(x => x.index / maxRecipientsPerAttempt)
            .Select(x => x.Select(y => y.intent).ToArray())
            .ToList();
    }

    private static List<PayoutIntent[]> CreatePaymentIdAwareGroups(CreatePayoutSendAttemptsRequest request,
        PayoutIntent[] orderedIntents)
    {
        if(request.IntegratedAddressPrefixes == null || request.IntegratedAddressPrefixes.Count == 0)
            throw new InvalidOperationException(
                "Payment-id-aware planning requires integrated address prefixes");

        var simpleIntents = new List<PayoutIntent>();
        var singletonIntents = new List<PayoutIntent[]>();

        foreach(var intent in orderedIntents)
        {
            ExtractAddressAndPaymentId(intent.Address, out var address, out var paymentId);

            if(paymentId != null || IsIntegratedAddress(address, request.IntegratedAddressPrefixes))
                singletonIntents.Add(new[] { intent });
            else
                simpleIntents.Add(intent);
        }

        var result = simpleIntents
            .Select((intent, index) => new { intent, index })
            .GroupBy(x => x.index / request.MaxRecipientsPerAttempt)
            .Select(x => x.Select(y => y.intent).ToArray())
            .ToList();

        result.AddRange(singletonIntents);
        return result;
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
                break;

            case PayoutSendShapes.AsyncOperation:
                if(request.MaxRecipientsPerAttempt <= 0)
                    throw new ArgumentOutOfRangeException(nameof(request.MaxRecipientsPerAttempt),
                        "Async-operation payout planning requires a maximum recipient count greater than zero");
                break;

            case PayoutSendShapes.AddressGroup:
                if(request.MaxRecipientsPerAttempt <= 0)
                    throw new ArgumentOutOfRangeException(nameof(request.MaxRecipientsPerAttempt),
                        "Address-group payout planning requires a maximum recipient count greater than zero");
                break;

            default:
                throw new ArgumentException($"Unsupported payout send shape '{request.SendShape}'", nameof(request));
        }

        switch(request.AttemptPlanningPolicy)
        {
            case PayoutProfileConstants.PlanningPolicies.Default:
            case PayoutProfileConstants.PlanningPolicies.ConcealPaymentIdAware:
            case PayoutProfileConstants.PlanningPolicies.CryptonotePaymentIdAware:
            case PayoutProfileConstants.PlanningPolicies.ZanoPaymentIdAware:
            case PayoutProfileConstants.PlanningPolicies.AlephiumGroupAware:
                break;

            default:
                throw new ArgumentException(
                    $"Unsupported payout attempt planning policy '{request.AttemptPlanningPolicy}'", nameof(request));
        }

        if(IsAlephiumGroupAwarePolicy(request.AttemptPlanningPolicy) && request.AddressGroupCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.AddressGroupCount),
                "Alephium group-aware payout planning requires an address group count greater than zero");
        }
    }

    private static bool IsPaymentIdAwarePlanningPolicy(string policy)
    {
        return string.Equals(policy, PayoutProfileConstants.PlanningPolicies.ConcealPaymentIdAware,
                   StringComparison.Ordinal) ||
               string.Equals(policy, PayoutProfileConstants.PlanningPolicies.CryptonotePaymentIdAware,
                   StringComparison.Ordinal) ||
               string.Equals(policy, PayoutProfileConstants.PlanningPolicies.ZanoPaymentIdAware,
                   StringComparison.Ordinal);
    }

    private static bool IsAlephiumGroupAwarePolicy(string policy)
    {
        return string.Equals(policy, PayoutProfileConstants.PlanningPolicies.AlephiumGroupAware,
            StringComparison.Ordinal);
    }

    // Groups intents by Alephium network group and chunks within each group by
    // MaxRecipientsPerAttempt. Attempts from different groups are never mixed.
    // Throws InvalidOperationException for any address that cannot be classified.
    private static List<PayoutIntent[]> CreateAlephiumGroupAwareGroups(CreatePayoutSendAttemptsRequest request,
        PayoutIntent[] orderedIntents)
    {
        var groupCount = request.AddressGroupCount;
        var groupBuckets = new List<PayoutIntent>[groupCount];
        for(var g = 0; g < groupCount; g++)
            groupBuckets[g] = new List<PayoutIntent>();

        foreach(var intent in orderedIntents)
        {
            var groupIndex = ClassifyAlephiumAddressGroup(intent.Address, groupCount);
            if(groupIndex == null)
                throw new InvalidOperationException(
                    $"Alephium: unclassifiable address in intent {intent.Id}, " +
                    $"batch {request.BatchId}, pool {request.PoolId}");

            groupBuckets[groupIndex.Value].Add(intent);
        }

        var result = new List<PayoutIntent[]>();
        for(var g = 0; g < groupCount; g++)
        {
            var bucket = groupBuckets[g];
            if(bucket.Count == 0)
                continue;

            for(var i = 0; i < bucket.Count; i += request.MaxRecipientsPerAttempt)
                result.Add(bucket.Skip(i).Take(request.MaxRecipientsPerAttempt).ToArray());
        }

        return result;
    }

    // Classifies an Alephium address into a configured network group.
    // Returns null for any address that cannot be safely classified (unclassifiable).
    //
    // Supported types (upstream protocol/script/LockupScript.scala):
    //   P2PKH (0): group via ScriptHint of 32-byte public-key hash
    //   P2MPKH(1): group via ScriptHint of first public-key hash
    //   P2SH  (2): group via ScriptHint of 32-byte script hash
    //   P2C   (3): group = last byte of 32-byte contract id when it is a valid group index
    //   P2PK/P2HMPK explicit suffix ":N": group = N when payload and suffix are valid
    private static int? ClassifyAlephiumAddressGroup(string address, int addressGroupCount)
    {
        if(string.IsNullOrWhiteSpace(address))
            return null;

        // P2PK / P2HMPK explicit grouped address: "base58payload:groupByte"
        var colonIdx = address.LastIndexOf(':');
        if(colonIdx >= 0)
        {
            var payload = address[..colonIdx];
            var suffix = address[(colonIdx + 1)..];
            return ClassifyAlephiumExplicitGroupAddress(payload, suffix, addressGroupCount);
        }

        var decoded = DecodeStandardBase58(address);
        if(decoded == null || decoded.Length == 0)
            return null;

        var typeByte = decoded[0];

        switch(typeByte)
        {
            case AlephiumTypeP2PKH:
            case AlephiumTypeP2SH:
                if(decoded.Length != AlephiumLockupScriptSerializedLength)
                    return null;

                // group from ScriptHint of the 32-byte hash (upstream ScriptHint.scala)
                return AlephiumGroupFromScriptHint(decoded.AsSpan(1, AlephiumHashLength), addressGroupCount);

            case AlephiumTypeP2MPKH:
                return ClassifyAlephiumP2MPKHGroup(decoded.AsSpan(1), addressGroupCount);

            case AlephiumTypeP2C:
                if(decoded.Length != AlephiumLockupScriptSerializedLength)
                    return null;

                // ContractId encodes the group in the last byte. It is not modulo-reduced.
                return decoded[32] < addressGroupCount ? (int?) decoded[32] : null;

            default:
                return null;
        }
    }

    private static int? ClassifyAlephiumP2MPKHGroup(ReadOnlySpan<byte> payload, int addressGroupCount)
    {
        var offset = 0;
        if(!TryReadAlephiumCompactSignedInt(payload, ref offset, out var publicKeyHashCount) ||
           publicKeyHashCount <= 0)
        {
            return null;
        }

        if(publicKeyHashCount > (payload.Length - offset) / AlephiumHashLength)
            return null;

        var firstPublicKeyHash = payload.Slice(offset, AlephiumHashLength);
        offset += publicKeyHashCount * AlephiumHashLength;

        if(!TryReadAlephiumCompactSignedInt(payload, ref offset, out var threshold) ||
           threshold < 1 ||
           threshold > publicKeyHashCount ||
           offset != payload.Length)
        {
            return null;
        }

        return AlephiumGroupFromScriptHint(firstPublicKeyHash, addressGroupCount);
    }

    private static int? ClassifyAlephiumExplicitGroupAddress(string payload, string suffix, int addressGroupCount)
    {
        if(string.IsNullOrEmpty(payload) || suffix.Length != 1 || suffix[0] < '0' || suffix[0] > '9')
            return null;

        var group = suffix[0] - '0';
        if(group < 0 || group >= addressGroupCount)
            return null;

        var decoded = DecodeStandardBase58(payload);
        if(decoded == null || decoded.Length != AlephiumGroupedKeySerializedLength)
            return null;

        return decoded[0] is AlephiumTypeP2PK or AlephiumTypeP2HMPK ? group : null;
    }

    // Alephium IntSerde delegates to CompactInteger.Signed.
    private static bool TryReadAlephiumCompactSignedInt(ReadOnlySpan<byte> bytes, ref int offset, out int value)
    {
        value = 0;
        if(offset < 0 || offset >= bytes.Length)
            return false;

        var first = bytes[offset];
        var mode = first & 0xc0;
        var size = mode switch
        {
            0x00 => 1,
            0x40 => 2,
            0x80 => 4,
            _ => (first & 0x3f) + 5
        };

        if(size <= 0 || bytes.Length - offset < size)
            return false;

        var body = bytes.Slice(offset, size);
        offset += size;

        if(mode == 0xc0)
        {
            if(size != 5)
                return false;

            value = BinaryPrimitives.ReadInt32BigEndian(body.Slice(1));
            return true;
        }

        var isPositive = (body[0] & 0x20) == 0;
        if(isPositive)
        {
            value = size switch
            {
                1 => body[0],
                2 => ((body[0] & 0x3f) << 8) | (body[1] & 0xff),
                4 => ((body[0] & 0x3f) << 24) |
                     ((body[1] & 0xff) << 16) |
                     ((body[2] & 0xff) << 8) |
                     (body[3] & 0xff),
                _ => 0
            };
        }
        else
        {
            value = size switch
            {
                1 => unchecked((int) ((uint) body[0] | 0xffffffc0u)),
                2 => unchecked((int) ((((uint) body[0] | 0xffffffc0u) << 8) |
                                      (uint) (body[1] & 0xff))),
                4 => unchecked((int) ((((uint) body[0] | 0xffffffc0u) << 24) |
                                      ((uint) (body[1] & 0xff) << 16) |
                                      ((uint) (body[2] & 0xff) << 8) |
                                      (uint) (body[3] & 0xff))),
                _ => 0
            };
        }

        return size is 1 or 2 or 4;
    }

    // ScriptHint.groupIndex (upstream util/ScriptHint.scala):
    //   value = DjbHash(hashBytes) | 1
    //   xorByte = byte0 ^ byte1 ^ byte2 ^ byte3   (each byte of the 32-bit value)
    //   group   = (xorByte & 0xff) % groupCount
    private static int AlephiumGroupFromScriptHint(ReadOnlySpan<byte> hashBytes, int addressGroupCount)
    {
        var scriptHint = AlephiumDjbHash(hashBytes) | 1;
        var xorByte = (byte)(scriptHint ^ (scriptHint >> 8) ^ (scriptHint >> 16) ^ (scriptHint >> 24));
        return (xorByte & 0xff) % addressGroupCount;
    }

    // DjbHash.intHash (upstream util/djb2/DjbHash.scala):
    //   start = 5381
    //   for each byte b: hash = ((hash << 5) + hash) + (b & 0xff)
    //   uses 32-bit signed int semantics (wraps on overflow)
    private static int AlephiumDjbHash(ReadOnlySpan<byte> bytes)
    {
        unchecked
        {
            var hash = 5381;
            foreach(var b in bytes)
                hash = ((hash << 5) + hash) + (b & 0xff);
            return hash;
        }
    }

    // Standard Bitcoin-style Base58 decoder (BigInteger-based, NOT CryptoNote block-based).
    // Leading '1' characters each decode to a 0x00 byte (as per Bitcoin Base58 convention).
    // Returns null for any invalid character or empty input.
    private static byte[] DecodeStandardBase58(string input)
    {
        if(string.IsNullOrEmpty(input))
            return null;

        var leadingZeros = 0;
        foreach(var c in input)
        {
            if(c != '1') break;
            leadingZeros++;
        }

        var value = BigInteger.Zero;
        foreach(var c in input)
        {
            var digit = CryptoNoteBase58Alphabet.IndexOf(c);
            if(digit < 0)
                return null;

            value = value * 58 + digit;
        }

        if(value.IsZero)
            return new byte[leadingZeros];

        var valueBytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);

        if(leadingZeros == 0)
            return valueBytes;

        var result = new byte[leadingZeros + valueBytes.Length];
        valueBytes.CopyTo(result, leadingZeros);
        return result;
    }

    private static void ExtractAddressAndPaymentId(string input, out string address, out string paymentId)
    {
        paymentId = null;
        var index = input.IndexOf(PayoutConstants.PayoutInfoSeperator);

        if(index == -1)
        {
            address = input;
            return;
        }

        address = input[..index];

        if(index + 1 >= input.Length)
            return;

        var candidate = input[(index + 1)..];
        if(candidate.Length == PayoutConstants.PaymentIdHexLength && candidate.All(IsHexCharacter))
            paymentId = candidate;
    }

    private static bool IsHexCharacter(char value)
    {
        return value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    private static bool IsIntegratedAddress(string address, IReadOnlyCollection<ulong> integratedAddressPrefixes)
    {
        var decoded = DecodeCryptoNoteAddress(address);

        // Some CryptoNote-family coins use the same prefix for standard and integrated
        // addresses. The planner must distinguish them by decoded payload length instead
        // of prefix alone.
        // Zano registers three integrated prefix families (addressPrefixIntegrated 13944,
        // addressV2PrefixIntegrated 14072, auditableAddressIntegratedPrefix 35401). All three
        // follow the standard CryptoNote integrated layout: 8-byte payment_id + 32-byte spend key
        // + 32-byte view key + 4-byte checksum = CryptoNoteIntegratedPayloadLength (76).
        // Standard Zano addresses (prefix 197) and auditable non-integrated addresses (prefix 39112)
        // use CryptoNoteStandardPayloadLength (68) and are not listed in IntegratedAddressPrefixes.
        if(decoded == null || !integratedAddressPrefixes.Contains(decoded.Value.Prefix))
            return false;

        return decoded.Value.PayloadLength switch
        {
            CryptoNoteIntegratedPayloadLength => true,
            CryptoNoteStandardPayloadLength => false,
            _ => false
        };
    }

    private static CryptoNoteAddressInfo? DecodeCryptoNoteAddress(string address)
    {
        if(string.IsNullOrWhiteSpace(address))
            return null;

        var decoded = DecodeCryptoNoteBase58(address);
        var prefix = decoded == null ? null : ReadVarInt(decoded);

        if(prefix == null)
            return null;

        return new CryptoNoteAddressInfo(prefix.Value.Value, decoded.Length - prefix.Value.BytesConsumed);
    }

    private static byte[] DecodeCryptoNoteBase58(string input)
    {
        var fullBlockCount = input.Length / CryptoNoteEncodedBlockSizes[8];
        var lastBlockSize = input.Length % CryptoNoteEncodedBlockSizes[8];
        var lastDecodedSize = Array.IndexOf(CryptoNoteEncodedBlockSizes, lastBlockSize);

        if(lastBlockSize > 0 && lastDecodedSize <= 0)
            return null;

        var resultSize = (fullBlockCount * 8) + Math.Max(lastDecodedSize, 0);
        var result = new byte[resultSize];
        var inputOffset = 0;
        var outputOffset = 0;

        for(var i = 0; i < fullBlockCount; i++)
        {
            if(!DecodeCryptoNoteBase58Block(input.AsSpan(inputOffset, CryptoNoteEncodedBlockSizes[8]), result,
                   outputOffset, 8))
                return null;

            inputOffset += CryptoNoteEncodedBlockSizes[8];
            outputOffset += 8;
        }

        if(lastBlockSize > 0 &&
           !DecodeCryptoNoteBase58Block(input.AsSpan(inputOffset, lastBlockSize), result, outputOffset,
               lastDecodedSize))
            return null;

        return result;
    }

    private static bool DecodeCryptoNoteBase58Block(ReadOnlySpan<char> input, byte[] output, int outputOffset,
        int decodedSize)
    {
        ulong value = 0;

        foreach(var c in input)
        {
            var digit = CryptoNoteBase58Alphabet.IndexOf(c);
            if(digit < 0)
                return false;

            if(value > (ulong.MaxValue - (ulong) digit) / 58ul)
                return false;

            value = (value * 58ul) + (ulong) digit;
        }

        for(var i = decodedSize - 1; i >= 0; i--)
        {
            output[outputOffset + i] = (byte) (value & 0xff);
            value >>= 8;
        }

        return value == 0;
    }

    private static CryptoNoteVarInt? ReadVarInt(byte[] bytes)
    {
        ulong result = 0;
        var shift = 0;
        var bytesConsumed = 0;

        foreach(var value in bytes)
        {
            bytesConsumed++;
            result |= (ulong) (value & 0x7f) << shift;

            if((value & 0x80) == 0)
                return new CryptoNoteVarInt(result, bytesConsumed);

            shift += 7;
            if(shift >= 64)
                return null;
        }

        return null;
    }

    private readonly record struct CryptoNoteVarInt(ulong Value, int BytesConsumed);

    private readonly record struct CryptoNoteAddressInfo(ulong Prefix, int PayloadLength);

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
