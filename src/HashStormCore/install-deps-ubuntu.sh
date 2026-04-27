#!/bin/bash

set -euo pipefail

if [[ ! -f /etc/os-release ]]; then
  echo "Unsupported system: /etc/os-release not found"
  exit 1
fi

source /etc/os-release

if [[ "${ID:-}" != "ubuntu" ]]; then
  echo "Unsupported distribution: expected Ubuntu, got '${ID:-unknown}'"
  exit 1
fi

SUDO=""
if [[ "${EUID}" -ne 0 ]]; then
  SUDO="sudo"
fi

case "${VERSION_ID:-}" in
  "22.04")
    # Ubuntu 22.04 requires the backports PPA for .NET 10 packages.
    ${SUDO} apt-get update
    ${SUDO} apt-get install -y software-properties-common
    ${SUDO} add-apt-repository -y ppa:dotnet/backports
    ;;
  "24.04")
    # Ubuntu 24.04 already exposes .NET 10 in the standard package feed.
    ;;
  *)
    echo "Unsupported Ubuntu version: ${VERSION_ID:-unknown}"
    echo "This script currently supports Ubuntu 22.04 and 24.04 only."
    exit 1
    ;;
esac

${SUDO} apt-get update

# ZeroMQ.dll loads the unversioned native library name 'libzmq',
# which on Ubuntu is provided by the libzmq3-dev symlink package.
${SUDO} apt-get install -y \
  dotnet-sdk-10.0 \
  git \
  cmake \
  clang \
  ninja-build \
  build-essential \
  libssl-dev \
  pkg-config \
  libboost-all-dev \
  libsodium-dev \
  libzmq5 \
  libzmq3-dev \
  libgmp-dev \
  libc++-dev \
  zlib1g-dev

echo "Ubuntu build dependencies installed successfully."
