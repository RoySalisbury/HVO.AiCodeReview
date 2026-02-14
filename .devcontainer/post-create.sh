#!/bin/bash
set -e
set -o pipefail


command_exists() {
	command -v "$1" >/dev/null 2>&1
}

echo "Running post-create setup..."

# Fix .dotnet directory ownership
echo "Fixing .dotnet directory ownership..."
sudo chown -R vscode:vscode /home/vscode/.dotnet || true

# Display .NET version and runtime details
echo "Checking .NET installation..."
dotnet --info
echo "Installed SDKs:"
dotnet --list-sdks || true
echo "Installed runtimes:"
dotnet --list-runtimes || true

echo "Checking Azure CLI installation..."
if command -v az >/dev/null 2>&1; then
	az version || true
else
	echo "Warning: Azure CLI (az) not found on PATH"
fi

echo "Checking additional toolbelt commands..."
if command_exists node; then node --version || true; else echo "Warning: node not found"; fi
if command_exists npm; then npm --version || true; else echo "Warning: npm not found"; fi
if command_exists python3; then python3 --version || true; else echo "Warning: python3 not found"; fi
if command_exists pwsh; then pwsh -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' || true; else echo "Warning: pwsh not found"; fi
if command_exists java; then java -version || true; else echo "Warning: java not found"; fi
if command_exists go; then go version || true; else echo "Warning: go not found"; fi
if command_exists terraform; then terraform version || true; else echo "Warning: terraform not found"; fi
if command_exists kubectl; then kubectl version --client=true --output=yaml 2>/dev/null | sed -n '1,25p' || true; else echo "Warning: kubectl not found"; fi
if command_exists helm; then helm version || true; else echo "Warning: helm not found"; fi

# Provision a workspace-local Python virtual environment with common tooling deps
echo "Setting up Python virtual environment and common parsing libraries..."
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENV_DIR="${REPO_ROOT}/.venv"
PY_REQS_FILE="${REPO_ROOT}/.devcontainer/python-requirements.txt"

if command_exists python3; then
	if [ ! -d "${VENV_DIR}" ]; then
		echo "Creating venv at ${VENV_DIR}"
		python3 -m venv "${VENV_DIR}" || {
			echo "Warning: python3 -m venv failed. Attempting to install python3-venv..."
			sudo apt-get update -y
			sudo apt-get install -y python3-venv || true
			python3 -m venv "${VENV_DIR}" || true
		}
	else
		echo "Venv already exists at ${VENV_DIR}"
	fi

	VENV_PY="${VENV_DIR}/bin/python"
	if [ -x "${VENV_PY}" ]; then
		"${VENV_PY}" -m pip install --upgrade pip setuptools wheel || true
		if [ -f "${PY_REQS_FILE}" ]; then
			echo "Installing Python requirements from ${PY_REQS_FILE}"
			"${VENV_PY}" -m pip install -r "${PY_REQS_FILE}" || true
		else
			echo "Warning: ${PY_REQS_FILE} not found; skipping Python package install"
		fi
	else
		echo "Warning: venv python not found at ${VENV_PY}; skipping Python package install"
	fi
else
	echo "Warning: python3 not found; skipping venv setup"
fi

# Ensure handy CLI tools are available (minimal set)
echo "Installing development CLI utilities..."
sudo apt-get update -y
sudo apt-get install -y jq ripgrep || echo "Warning: CLI utility installation failed, continuing..."

# Install EF Core CLI matching the repo packages
EF_TOOLS_VERSION="10.0.*"
echo "Installing dotnet-ef $EF_TOOLS_VERSION..."
dotnet tool update --global dotnet-ef --version "$EF_TOOLS_VERSION" 2>/dev/null \
	|| dotnet tool install --global dotnet-ef --version "$EF_TOOLS_VERSION"

# Add vscode user to docker group
echo "Adding vscode user to docker group..."
if getent group docker >/dev/null 2>&1; then
	sudo usermod -aG docker vscode || true
else
	echo "Docker group not present; skipping usermod"
fi

# Set docker socket permissions
echo "Setting docker socket permissions..."
if [ -S /var/run/docker.sock ]; then
	sudo chmod 666 /var/run/docker.sock || true
else
	echo "Docker socket not present; skipping chmod"
fi

# Verify docker is working
echo "Verifying Docker installation..."
if command_exists docker; then
	docker --version
else
	echo "Warning: docker CLI not found on PATH"
fi

# Setup SSH agent
echo "Setting up SSH agent..."
if [ -z "$SSH_AUTH_SOCK" ]; then
	echo "Starting new SSH agent..."
	eval "$(ssh-agent -s)"
else
	echo "Using existing SSH agent at $SSH_AUTH_SOCK"
fi

# Try to load SSH keys if available
if compgen -G "/home/vscode/.ssh/id_*" >/dev/null 2>&1; then
	for key in /home/vscode/.ssh/id_*; do
		if [[ -f "$key" && "$key" != *.pub ]]; then
			if ssh-add "$key" >/dev/null 2>&1; then
				echo "Loaded SSH key: $key"
			else
				echo "Warning: Failed to load key $key (may require passphrase)"
			fi
		fi
	done
else
	echo "No default SSH keys found. You can add keys manually with ssh-add if needed."
fi

ensure_docker_context() {
	local name="$1"
	local description="$2"
	local host="$3"
	if docker context inspect "$name" >/dev/null 2>&1; then
		echo "Context '$name' already present."
	else
		echo "Creating docker context '$name' (${description})"
		docker context create "$name" --description "$description" --docker "host=$host"
	fi
}

ensure_docker_context "proxmox-home" "Remote engine on Home Proxmox" "ssh://roys@192.168.2.104"
ensure_docker_context "rpi-home" "Remote engine on Home Raspberry Pi" "ssh://roys@192.168.2.21"

echo

# Generate HTTPS developer certificate
echo "Generating HTTPS developer certificate..."
dotnet dev-certs https --clean
dotnet dev-certs https


echo "Post-create setup completed successfully!"
