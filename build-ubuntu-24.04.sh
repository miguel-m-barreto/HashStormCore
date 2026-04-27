#!/bin/bash

# .NET 10 is available in the Ubuntu 24.04 package feed.

# install dev-dependencies
sudo apt-get update; \
  # ZeroMQ.dll P/Invokes the unversioned native library name "libzmq",
  # which on Ubuntu is provided by the dev package symlink.
  sudo apt-get -y install dotnet-sdk-10.0 git cmake clang ninja-build build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5 libzmq3-dev libgmp-dev libc++-dev zlib1g-dev

(cd src/HashStormCore && \
BUILDIR=${1:-../../build} && \
echo "Building into $BUILDIR" && \
dotnet publish -c Release --framework net10.0 -o $BUILDIR)
