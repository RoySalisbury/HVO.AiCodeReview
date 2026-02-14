# ===========================================================================
# HVO.AiCodeReview API -- Multi-stage Docker build
# ===========================================================================
# Build (from repo root):
#   docker build -f src/HVO.AiCodeReview/Dockerfile -t hvo-ai-code-review .
# Run:
#   docker run -p 8080:8080 -e AzureDevOps__PersonalAccessToken=xxx hvo-ai-code-review
# ===========================================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore first (for layer caching)
COPY src/HVO.AiCodeReview/HVO.AiCodeReview.csproj src/HVO.AiCodeReview/
RUN dotnet restore src/HVO.AiCodeReview/HVO.AiCodeReview.csproj

# Copy everything else and publish
COPY src/HVO.AiCodeReview/ src/HVO.AiCodeReview/
RUN dotnet publish src/HVO.AiCodeReview/HVO.AiCodeReview.csproj -c Release -o /app/publish --no-restore

# ---------------------------------------------------------------------------
# Runtime image
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root user for security (explicit home directory avoids runtime issues)
RUN adduser --disabled-password --gecos "" --home /app appuser
USER appuser

COPY --from=build /app/publish .

# ASP.NET Core listens on 8080 by default in .NET 8 containers
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "HVO.AiCodeReview.dll"]
