# HashStormCore.LiveAggregator

Consumes batched share events from Redis Streams and writes Redis live read models. State is rebuilt from the retained stream window when available; otherwise the service reports `warming_up` until enough live events arrive.
