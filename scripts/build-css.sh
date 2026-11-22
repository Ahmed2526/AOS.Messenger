#!/usr/bin/env bash
set -euo pipefail

VERSION="v4.3.5"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

TOOLS_DIR="${REPO_ROOT}/.tools"
mkdir -p "${TOOLS_DIR}"

TAILWIND_BIN="${TOOLS_DIR}/tailwindcss"

if [ ! -f "${TAILWIND_BIN}" ]; then
  OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
  ARCH="$(uname -m)"

  case "${OS}" in
    linux*)
      case "${ARCH}" in
        x86_64) ASSET="tailwindcss-linux-x64" ;;
        aarch64|arm64) ASSET="tailwindcss-linux-arm64" ;;
        *) echo "Unsupported architecture: ${ARCH}"; exit 1 ;;
      esac
      ;;
    darwin*)
      case "${ARCH}" in
        x86_64) ASSET="tailwindcss-macos-x64" ;;
        arm64) ASSET="tailwindcss-macos-arm64" ;;
        *) echo "Unsupported architecture: ${ARCH}"; exit 1 ;;
      esac
      ;;
    *)
      echo "Unsupported OS: ${OS}"; exit 1 ;;
  esac

  echo "Downloading Tailwind CSS standalone CLI (${VERSION}) for ${OS}-${ARCH}..."
  DOWNLOAD_URL="https://github.com/tailwindlabs/tailwindcss/releases/download/${VERSION}/${ASSET}"
  curl -sL -o "${TAILWIND_BIN}" "${DOWNLOAD_URL}"
  chmod +x "${TAILWIND_BIN}"
fi

echo "Compiling Tailwind CSS..."
"${TAILWIND_BIN}" -i src/AOS.Messenger.Web/Styles/app.css -o src/AOS.Messenger.Web/wwwroot/css/app.css --minify
echo "Tailwind CSS compilation complete: src/AOS.Messenger.Web/wwwroot/css/app.css"
