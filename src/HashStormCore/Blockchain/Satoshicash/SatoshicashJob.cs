using System.Globalization;
using System.Text;
using HashStormCore.Blockchain.Bitcoin;
using HashStormCore.Blockchain.Bitcoin.Configuration;
using HashStormCore.Blockchain.Bitcoin.DaemonResponses;
using HashStormCore.Blockchain.Satoshicash.DaemonResponses;
using HashStormCore.Configuration;
using HashStormCore.Crypto;
using HashStormCore.Extensions;
using HashStormCore.Native;
using HashStormCore.Stratum;
using HashStormCore.Time;
using HashStormCore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using NLog;
using Contract = HashStormCore.Contracts.Contract;

namespace HashStormCore.Blockchain.Satoshicash;

public class SatoshicashJob : BitcoinJob
{
    protected virtual byte[] SerializeHeader(Span<byte> coinbaseHash, uint nTime, uint nonce, byte[] hashRandomXSCash)
    {
        // build merkle-root
        var merkleRoot = mt.WithFirst(coinbaseHash.ToArray());

        var blockHeader = new SatoshicashBlockHeader
        {
            Version = (int) BlockTemplate.Version,
            Bits = new Target(Encoders.Hex.DecodeData(BlockTemplate.Bits)),
            HashPrevBlock = uint256.Parse(BlockTemplate.PreviousBlockhash),
            HashMerkleRoot = new uint256(merkleRoot),
            NTime = nTime,
            Nonce = nonce,
            HashRandomXSCash = hashRandomXSCash
        };

        return blockHeader.ToBytes();
    }

    public virtual (Share Share, string BlockHex) ProcessShareInternal(
        StratumConnection worker, string extraNonce2, uint nTime, uint nonce)
    {
        var context = worker.ContextAs<SatoshicashWorkerContext>();
        var extraNonce1 = context.ExtraNonce1;

        // build coinbase
        var coinbase = SerializeCoinbase(extraNonce1, extraNonce2);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        // serialize clean block-header
        var cleanHeaderBytes = SerializeHeader(coinbaseHash, nTime, nonce, sha256Empty);

        // hash RandomXSCash
        Span<byte> hashRandomXSCashBytes = stackalloc byte[32];
        RandomXSCash.CalculateHash(RandomXSCashRealm, SeedHash, cleanHeaderBytes, hashRandomXSCashBytes);

        // hash Commitment
        Span<byte> commitmentRandomXSCashBytes = stackalloc byte[32];
        RandomXSCash.CalculateCommitment(cleanHeaderBytes, hashRandomXSCashBytes, commitmentRandomXSCashBytes);
        var commitmentRandomXSCashValue = new uint256(commitmentRandomXSCashBytes);

        // calc share-diff
        var shareDiff = (double) new BigRational(BitcoinConstants.Diff1, commitmentRandomXSCashBytes.ToBigInteger()) * shareMultiplier;
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        // check if the share meets the much harder block difficulty (block candidate)
        var isBlockCandidate = commitmentRandomXSCashValue <= blockTargetValue;

        // test if share meets at least workers current difficulty
        if(!isBlockCandidate && ratio < 0.99)
        {
            // check if share matched the previous difficulty from before a vardiff retarget
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;

                if(ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                // use previous difficulty
                stratumDifficulty = context.PreviousDifficulty.Value;
            }

            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }

        var result = new Share
        {
            BlockHeight = BlockTemplate.Height,
            NetworkDifficulty = Difficulty,
            Difficulty = stratumDifficulty / shareMultiplier,
        };

        if(isBlockCandidate)
        {
            result.IsBlockCandidate = true;
            
            // serialize block-header
            var headerBytes = SerializeHeader(coinbaseHash, nTime, nonce, hashRandomXSCashBytes.ToArray());

            // hash block-header
            Span<byte> blockHash = stackalloc byte[32];
            blockHasher.Digest(headerBytes, blockHash, nTime);
            result.BlockHash = blockHash.ToHexString();

            var blockBytes = SerializeBlock(headerBytes, coinbase);
            var blockHex = blockBytes.ToHexString();

            return (result, blockHex);
        }

        return (result, null);
    }

    #region RandomXSCash

    protected RandomXSCashExtra randomXSCashParameters;
    public string RandomXSCashRealm { get; protected set; }
    public string SeedHash { get; protected set; }

    #endregion //RandomXSCash

    #region API-Surface

    public virtual void Init(BlockTemplate blockTemplate, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig,
        ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network,
        bool isPoS, double shareMultiplier, IHashAlgorithm coinbaseHasher,
        IHashAlgorithm headerHasher, IHashAlgorithm blockHasher,
        string randomXSCashRealm)
    {
        Contract.RequiresNonNull(blockTemplate);
        Contract.RequiresNonNull(pc);
        Contract.RequiresNonNull(cc);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(poolAddressDestination);
        Contract.RequiresNonNull(coinbaseHasher);
        Contract.RequiresNonNull(headerHasher);
        Contract.RequiresNonNull(blockHasher);
        Contract.RequiresNonNull(randomXSCashRealm);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));

        this.coin = pc.Template.As<BitcoinTemplate>();
        this.txVersion = coin.CoinbaseTxVersion;
        this.network = network;
        this.clock = clock;
        this.poolAddressDestination = poolAddressDestination;
        this.BlockTemplate = blockTemplate;
        this.JobId = jobId;

        var coinbaseString = !string.IsNullOrEmpty(cc.PaymentProcessing?.CoinbaseString) ?
            cc.PaymentProcessing?.CoinbaseString.Trim() : "HashStormCore";

        if(!string.IsNullOrEmpty(coinbaseString))
            this.scriptSigFinalBytes = new Script(Op.GetPushOp(Encoding.UTF8.GetBytes(coinbaseString))).ToBytes();

        this.Difficulty = new Target(System.Numerics.BigInteger.Parse(BlockTemplate.Target, NumberStyles.HexNumber)).Difficulty;

        extraNoncePlaceHolderLength = BitcoinConstants.ExtranoncePlaceHolderLength;
        this.isPoS = isPoS;
        this.shareMultiplier = shareMultiplier;

        txComment = !string.IsNullOrEmpty(extraPoolConfig?.CoinbaseTxComment) ?
            extraPoolConfig.CoinbaseTxComment : coin.CoinbaseTxComment;

        if(coin.HasRandomXSCash)
            randomXSCashParameters = BlockTemplate.Extra.SafeExtensionDataAs<RandomXSCashExtra>();

        this.coinbaseHasher = coinbaseHasher;
        this.headerHasher = headerHasher;
        this.blockHasher = blockHasher;
        this.RandomXSCashRealm = randomXSCashRealm;
        this.SeedHash = SatoshicashUtils.GenerateEpochSeedHash(randomXSCashParameters.EpochDurationRandomXSCash, BlockTemplate.CurTime, coin.HeaderHasherValue);
        
        if(!string.IsNullOrEmpty(BlockTemplate.Target))
            this.blockTargetValue = new uint256(BlockTemplate.Target);
        else
        {
            var tmp = new Target(BlockTemplate.Bits.HexToByteArray());
            this.blockTargetValue = tmp.ToUInt256();
        }

        previousBlockHashReversedHex = BlockTemplate.PreviousBlockhash
            .HexToByteArray()
            .ReverseByteOrder()
            .ToHexString();

        BuildMerkleBranches();
        BuildCoinbase();

        jobParams = new object[]
        {
            JobId,
            previousBlockHashReversedHex,
            coinbaseInitialHex,
            coinbaseFinalHex,
            merkleBranchesHex,
            BlockTemplate.Version.ToStringHex8(),
            BlockTemplate.Bits,
            BlockTemplate.CurTime.ToStringHex8(),
            false
        };
    }

    public virtual (Share Share, string BlockHex) ProcessShare(StratumConnection worker,
        string extraNonce2, string nTime, string nonce)
    {
        var context = worker.ContextAs<SatoshicashWorkerContext>();
        var expectedExtraNonce2Length = extraNoncePlaceHolderLength * 2 - (context.ExtraNonce1?.Length ?? 0);

        if(string.IsNullOrEmpty(extraNonce2) || extraNonce2.Length != expectedExtraNonce2Length)
            throw new StratumException(StratumError.Other, "incorrect size of extranonce2");

        if(!HexUtils.IsFixedLengthHex(extraNonce2, expectedExtraNonce2Length))
            throw new StratumException(StratumError.Other, "invalid extranonce2");

        // validate nTime
        if(string.IsNullOrEmpty(nTime) || nTime.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of ntime");

        if(!HexUtils.IsFixedLengthHex(nTime, 8) || !uint.TryParse(nTime, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nTimeInt))
            throw new StratumException(StratumError.Other, "invalid ntime");

        if(nTimeInt < BlockTemplate.CurTime || nTimeInt > ((DateTimeOffset) clock.Now).ToUnixTimeSeconds() + 7200)
            throw new StratumException(StratumError.Other, "ntime out of range");

        // validate nonce
        if(string.IsNullOrEmpty(nonce) || nonce.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of nonce");

        if(!HexUtils.IsFixedLengthHex(nonce, 8) || !uint.TryParse(nonce, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nonceInt))
            throw new StratumException(StratumError.Other, "invalid nonce");

        // dupe check
        if(!RegisterSubmit(context.ExtraNonce1, extraNonce2, nTime, nonce))
            throw new StratumException(StratumError.DuplicateShare, "duplicate");

        return ProcessShareInternal(worker, extraNonce2, nTimeInt, nonceInt);
    }

    #endregion // API-Surface
}
