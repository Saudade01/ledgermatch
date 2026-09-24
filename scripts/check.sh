#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${RECON_TEST_DB:?Set RECON_TEST_DB to a local PostgreSQL connection with CREATEDB permission.}"
dotnet restore Mutabakat.slnx
dotnet format Mutabakat.slnx --verify-no-changes --no-restore
dotnet build Mutabakat.slnx -c Release --no-restore
dotnet test Mutabakat.slnx -c Release --no-build
