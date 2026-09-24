#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${RECON_TEST_DB:?Set RECON_TEST_DB to a local PostgreSQL connection with CREATEDB permission.}"
dotnet restore LedgerMatch.slnx
dotnet format LedgerMatch.slnx --verify-no-changes --no-restore
dotnet build LedgerMatch.slnx -c Release --no-restore
dotnet test LedgerMatch.slnx -c Release --no-build
