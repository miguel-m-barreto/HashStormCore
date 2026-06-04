# Bitcoin RPC Payout Sidecar

Bitcoin RPC payout execution runs in `HashStormCore.PayoutProcessor`, not in Pool Core. Enabling it in the sidecar does not wire senders into the Stratum, `mining.submit`, or block submission critical path.

## Default Safety

The payout processor remains non-sending by default:

- `enabled=false` disables the sidecar.
- `mode=Disabled` and `mode=DryRun` do not create real payout sender registrations.
- `fakeAdaptersOnly=true` forces the Bitcoin RPC sender registry to stay empty.
- Empty or disabled `bitcoinRpcAdapters` entries do not create senders.
- Invalid enabled Bitcoin RPC adapter config fails startup instead of partially registering senders.

## Required Gates

Bitcoin RPC senders are materialized only when every gate is satisfied:

- `enabled=true`
- `mode=DbMutating`
- `fakeAdaptersOnly=false`
- `bitcoinRpcAdapters[n].enabled=true`
- the configured cluster pool exists
- the cluster pool is enabled
- `paymentProcessing.enabled=true`
- `paymentProcessing.engine=intent`
- the pool coin matches the adapter coin
- the resolved payout profile is reservation-ready and uses the `bitcoin-rpc` adapter

## Supported Methods

Current Bitcoin RPC sender support is limited to transparent txid payout flows:

- `batch_multi_recipient` profile shape with `sendmany`
- `per_address` profile shape with `sendtoaddress`

The sender records txid evidence only. Settlement later consumes validated txid evidence and creates payment rows, negative balance changes, and settled intents.

## Secret Hygiene

Do not put credentials in endpoint URI userinfo. This is rejected:

```text
http://rpc-user:secret@127.0.0.1:8332
```

Use separate `username` and `password` fields. Startup diagnostics, validation errors, and safe summaries redact endpoint, username, password, raw wallet name, authorization headers, and JSON-RPC payloads. Prefer environment variables or a secrets manager for real deployments.

## Minimal Disabled Example

`configs/payout-processor.example.json` is safe by default. The Bitcoin RPC entry is disabled and `fakeAdaptersOnly` remains `true`.

```json
{
  "enabled": false,
  "mode": "Disabled",
  "fakeAdaptersOnly": true,
  "bitcoinRpcAdapters": [
    {
      "enabled": false,
      "poolId": "example-bitcoin-pool",
      "coin": "bitcoin",
      "endpoint": "http://127.0.0.1:8332",
      "username": "bitcoin-rpc-user",
      "password": "change-me",
      "walletName": "optional-wallet-name",
      "requestTimeoutSeconds": 30,
      "allowSendMany": true,
      "allowSendToAddress": true
    }
  ]
}
```

## Minimal Enabled Shape

Only use this after validating pool configuration, wallet routing, and operational monitoring in a non-production environment:

```json
{
  "enabled": true,
  "mode": "DbMutating",
  "fakeAdaptersOnly": false,
  "bitcoinRpcAdapters": [
    {
      "enabled": true,
      "poolId": "your-bitcoin-pool",
      "coin": "bitcoin",
      "endpoint": "http://127.0.0.1:8332",
      "username": "bitcoin-rpc-user",
      "password": "change-me",
      "walletName": "optional-wallet-name",
      "requestTimeoutSeconds": 30,
      "allowSendMany": true,
      "allowSendToAddress": true
    }
  ]
}
```

The matching Pool Core pool must be enabled and must use intent payment processing:

```json
{
  "id": "your-bitcoin-pool",
  "enabled": true,
  "coin": "bitcoin",
  "paymentProcessing": {
    "enabled": true,
    "engine": "intent"
  }
}
```

## Explicit Limitations

- No wallet unlock/passphrase flow is implemented.
- No subtract-fee or miners-pay-fees mode is implemented.
- Startup does not perform a real daemon health check.
- Bitcoin RPC does not use operation-id reconciliation.
- HTTP and transport failures become ambiguous review states and do not settle or debit balances.
