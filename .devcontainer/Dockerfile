# See here for image contents: https://github.com/devcontainers/images/tree/main/src/dotnet
FROM mcr.microsoft.com/devcontainers/dotnet:1-10.0

# The base image currently includes a Yarn APT source (dl.yarnpkg.com) but does not
# include the corresponding signing key, which causes apt operations to fail with:
#   NO_PUBKEY 62D54FD4003F6525
# We don't need the Yarn APT repo for this repo (Node feature provides Corepack),
# so remove it to keep apt stable for both feature installs and post-create steps.
RUN set -e; \
    rm -f /etc/apt/sources.list.d/yarn.list || true; \
    rm -f /etc/apt/sources.list.d/yarn.sources || true
