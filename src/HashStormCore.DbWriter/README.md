# HashStormCore.DbWriter

Consumes Redis Streams share-event batches and writes share rows to PostgreSQL after a successful batch read. Stream messages are acknowledged only after the database write succeeds.
