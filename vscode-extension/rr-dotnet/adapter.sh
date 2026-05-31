#!/usr/bin/env bash
set -euo pipefail

script_path="$(readlink -f "${BASH_SOURCE[0]}")"
extension_dir="$(cd "$(dirname "$script_path")" && pwd)"
repo_root="$(cd "$extension_dir/../.." && pwd)"

exec dotnet "$repo_root/src/RrDotNet.Dap/bin/Debug/net10.0/RrDotNet.Dap.dll"
