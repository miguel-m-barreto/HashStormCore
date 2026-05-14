FROM mcr.microsoft.com/dotnet/sdk:10.0-jammy as BUILDER
WORKDIR /app
RUN apt-get update && \
    apt-get -y install cmake clang ninja-build build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5 libzmq3-dev golang-go libgmp-dev libc++-dev zlib1g-dev
COPY . .
WORKDIR /app/src/HashStormCore
RUN dotnet publish -c Release --framework net10.0 -o ../../build

FROM mcr.microsoft.com/dotnet/aspnet:10.0-jammy
WORKDIR /app
RUN apt-get update && \
    apt-get install -y libzmq5 libzmq3-dev libsodium-dev curl && \
    apt-get clean
EXPOSE  4000-4090
COPY --from=BUILDER /app/build ./
RUN mkdir -p /app/logs /app/data/event-outbox && \
    touch /app/recovered-shares.txt && \
    chown -R $APP_UID:$APP_UID /app/logs /app/data /app/recovered-shares.txt
USER $APP_UID
CMD ["./HashStormCore", "-c", "config.json" ]
