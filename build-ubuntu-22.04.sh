#!/bin/bash

# .NET 10 is available for Ubuntu 22.04 via the Ubuntu backports feed.

sudo apt-get update; \
  sudo apt-get -y install software-properties-common

sudo add-apt-repository -y ppa:dotnet/backports

# install dev-dependencies
sudo apt-get update; \
  sudo apt-get -y install dotnet-sdk-10.0 git cmake clang ninja-build build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5 libgmp-dev libc++-dev zlib1g-dev

(cd src/HashStormCore && \
BUILDIR=${1:-../../build} && \
echo "Building into $BUILDIR" && \
dotnet publish -c Release --framework net10.0 -o $BUILDIR)
