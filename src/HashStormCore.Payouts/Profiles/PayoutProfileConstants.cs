using HashStormCore.Persistence.Model;

namespace HashStormCore.Payouts.Profiles;

public static class PayoutProfileConstants
{
    public static class Families
    {
        public const string Alephium = "alephium";
        public const string Beam = "beam";
        public const string Bitcoin = "bitcoin";
        public const string Conceal = "conceal";
        public const string Cryptonote = "cryptonote";
        public const string Equihash = "equihash";
        public const string Ergo = "ergo";
        public const string Ethereum = "ethereum";
        public const string Handshake = "handshake";
        public const string Kaspa = "kaspa";
        public const string Nexa = "nexa";
        public const string Progpow = "progpow";
        public const string Satoshicash = "satoshicash";
        public const string Warthog = "warthog";
        public const string Xelis = "xelis";
        public const string Zano = "zano";
    }

    public static class AdapterIds
    {
        public const string BitcoinRpc = "bitcoin-rpc";
        public const string HandshakeWalletRpc = "handshake-wallet-rpc";
        public const string EquihashZAsync = "equihash-z-async";
        public const string EquihashBitcoinRpc = "equihash-bitcoin-rpc";
        public const string CryptonoteWalletRpc = "cryptonote-wallet-rpc";
        public const string ConcealWalletRpc = "conceal-wallet-rpc";
        public const string ZanoWalletRpc = "zano-wallet-rpc";
        public const string EthereumRpc = "ethereum-rpc";
        public const string ErgoWalletApi = "ergo-wallet-api";
        public const string BeamWalletRpc = "beam-wallet-rpc";
        public const string AlephiumWalletApi = "alephium-wallet-api";
        public const string KaspaWalletWrapper = "kaspa-wallet-wrapper";
        public const string XelisWalletRpc = "xelis-wallet-rpc";
        public const string WarthogRestSigned = "warthog-rest-signed";
    }

    public static class SendMethods
    {
        public const string SendMany = "sendmany";
        public const string SendToAddress = "sendtoaddress";
        public const string ZSendMany = "z_sendmany";
        public const string Transfer = "transfer";
        public const string TransferSplit = "transfer_split";
        public const string SendTransaction = "sendTransaction";
        public const string EthSendTransaction = "eth_sendTransaction";
        public const string WalletPaymentTransactionGenerateAndSend = "walletPaymentTransactionGenerateAndSend";
        public const string BeamSendTransaction = "send_transaction";
        public const string BuildSignSubmit = "build_sign_submit";
        public const string KaspaSend = "/send";
        public const string BuildTransaction = "build_transaction";
        public const string TransactionAdd = "transaction/add";
    }

    public static class SettlementEvidenceKinds
    {
        public const string TxId = PayoutExternalConfirmationKinds.TxId;
        public const string RawHash = PayoutExternalConfirmationKinds.RawHash;
        public const string OperationIdThenTxId = "operationid_then_txid";
        public const string UnsafePlaceholder = "unsafe_placeholder";
        public const string Unsupported = "unsupported";
    }

    public static class SendShapes
    {
        public const string BatchMultiRecipient = PayoutSendShapes.BatchMultiRecipient;
        public const string PerAddress = PayoutSendShapes.PerAddress;
        public const string AddressGroup = PayoutSendShapes.AddressGroup;
        public const string AsyncOperation = PayoutSendShapes.AsyncOperation;
    }

    public static class PlanningPolicies
    {
        public const string Default = "default";
        public const string ConcealPaymentIdAware = "conceal_payment_id_aware";
        public const string CryptonotePaymentIdAware = "cryptonote_payment_id_aware";
        public const string ZanoPaymentIdAware = "zano_payment_id_aware";
    }

    public static class MultiHashEvidencePolicies
    {
        public const string NotApplicable = "not_applicable";
        public const string Unsupported = "unsupported";
    }
}
