## Legacy ShareRelay / ShareReceiver

**Composed by:** DankMiner  
**Inspiration:** Based on insights and studies from the miningcore GitHub repository.

---

### Current Architecture Notice

ShareRelay is a legacy, best-effort ZMQ relay path. It can be useful for
telemetry-style aggregation or older deployments, but it is not the
accounting-critical durability path in the current HashStormCore architecture.

When `eventPipeline.enabled=true`, accepted-share and block-candidate accounting
must use the Pool Core event pipeline, WAL/outbox, Redis Streams, and sidecars.
Do not use ShareRelay to bypass or replace that event pipeline.

---

### Purpose

The legacy goal is to enable a setup where **multiple stratum servers** publish
share data toward a **shared primary receiver**. This architecture can help with:

- **Distribute load:** Different servers can handle mining requests concurrently.
- **Operational redundancy:** If one stratum server goes down, the others can continue operating.
- **Enhance security:** A shared encryption key ensures that communication between all parts of the system is protected.
- **Centralize management:** The primary server aggregates data from individual stratum servers, which simplifies monitoring and control.

This does not provide the same replay, idempotency, or local durability guarantee
as the event pipeline.

---

### Configuration Details

#### On the Primary Server

In your primary server’s configuration file—typically `config.json`—you’ll add the section `"shareRelays"`. This is an array containing the details for each stratum server.

**Example:**

```json
"shareRelays": [
    {
        "url": "tcp://stratum-a.example.com:6000",
        "curveServerPublicKey": "SERVER_PUBLIC_KEY_Z85_40_CHARS",
        "curveClientSecretKey": "CLIENT_A_SECRET_KEY_Z85_40_CHARS"
    },
    {
        "url": "tcp://stratum-b.example.com:6000",
        "curveServerPublicKey": "SERVER_PUBLIC_KEY_Z85_40_CHARS",
        "curveClientSecretKey": "CLIENT_B_SECRET_KEY_Z85_40_CHARS"
    },
    {
        "url": "tcp://stratum-c.example.com:6000",
        "curveServerPublicKey": "SERVER_PUBLIC_KEY_Z85_40_CHARS",
        "curveClientSecretKey": "CLIENT_C_SECRET_KEY_Z85_40_CHARS"
    }
]
```

**Steps:**

1. **Replace the URL:** For each entry, change the example hostname with the actual IP address or domain name of the stratum server.
2. **Configure CURVE keys:** Generate one ZeroMQ CURVE keypair for the relay server and one keypair for each stratum client. Put the server public key in `curveServerPublicKey`, and put each client secret key in that client's `curveClientSecretKey`.

#### On Each Stratum Server

Similarly, on each individual stratum server, you must configure a corresponding section, labeled `"shareRelay"`. This tells the server where to publish its data and what key to use for secure communication.

**Example:**

```json
"shareRelay": {
    "publishUrl": "tcp://stratum-a.example.com:6000",
    "curveServerSecretKey": "SERVER_SECRET_KEY_Z85_40_CHARS",
    "allowedClientPublicKeys": [
        "CLIENT_A_PUBLIC_KEY_Z85_40_CHARS"
    ]
}
```

**Steps:**

1. **Update the Publish URL:** Change `publishUrl` to reflect that server's public IP address or hostname.
2. **Ensure Matching Encryption:** Put the server secret key only on the `shareRelay` server. Put the matching server public key on receivers. Put each receiver's client public key in `allowedClientPublicKeys` and the matching client secret key only on that receiver.

---

### Why Do We Do This?

1. **Centralized Data Collection:**  
   By linking multiple stratum servers to a single primary receiver, mining data is gathered and consolidated in one place. This makes it easier to analyze performance, detect issues, and manage operations without having to access each server individually.

2. **Enhanced Security:**  
   The use of a shared encryption key across all communications is vital. It ensures that data transferred between the servers is encrypted, thwarting unauthorized access and potential tampering. This is especially important in mining operations where sensitive data is exchanged.

3. **Load Balancing and Fault Tolerance:**  
   Having a distributed network of stratum servers can handle a larger volume of mining requests and distribute the load. If one server encounters problems or needs maintenance, the primary receiver can continue to receive data from the remaining nodes.

4. **Scalability:**  
   This configuration is scalable. As mining operations grow, additional stratum servers can be added with minimal adjustments—just update the primary server’s configuration and add a corresponding `shareRelay` on the new node. This seamless scalability helps accommodate growth without significant re-engineering.

---

In summary, this legacy design can help manage multiple mining servers, but it is
best-effort. For current HashStormCore deployments that need accepted-share and
block-candidate durability, use the event pipeline instead.

---

**Extra Insights:** If you’re looking to further optimize your mining setup, consider integrating automated monitoring tools on your primary server. These tools can alert you immediately if a relay goes offline or encounters errors, allowing for rapid intervention. Moreover, periodically updating your encryption keys and rotating them on a scheduled basis can add an extra layer of security to your communication channels.
