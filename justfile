set windows-shell := ["pwsh.exe", "-NoLogo", "-Command"]

solution := "TemporalAgents.slnx"
configuration := "Release"
artifacts_dir := "artifacts/packages"
unit_tests_dir := "tests/Temporalio.Extensions.Agents.Tests"
integration_tests_dir := "tests/Temporalio.Extensions.Agents.IntegrationTests"

version := `dotnet tool run minver --default-pre-release-identifiers preview`

# List available recipes
default:
    @just --list

# Show project info
info:
    @echo "Solution  : {{solution}}"
    @echo "Version   : {{version}}"
    @echo "Config    : {{configuration}}"
    @echo "Artifacts : {{artifacts_dir}}"

# Remove all build output
clean: clean-source clean-tests clean-samples
    @echo "Clean complete."

# Clean source projects
clean-source:
    dotnet clean {{solution}} --configuration {{configuration}} --nologo -v q

# Clean test output directories
clean-tests:
    dotnet clean {{unit_tests_dir}} --configuration {{configuration}} --nologo -v q
    dotnet clean {{integration_tests_dir}} --configuration {{configuration}} --nologo -v q

# Clean sample projects
clean-samples:
    dotnet clean samples --configuration {{configuration}} --nologo -v q 2>/dev/null || true

# Restore NuGet packages
restore:
    dotnet restore {{solution}}

# Build in Release (default)
build: restore
    dotnet build {{solution}} --configuration {{configuration}} --no-restore

# Build in Debug
build-debug: restore
    dotnet build {{solution}} --configuration Debug --no-restore

# Run unit tests only (no Temporal server required)
test-unit: build
    dotnet test {{unit_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal"

# Run integration tests only (requires: temporal server start-dev)
test-integration: build
    @echo "NOTE: Integration tests require a running Temporal server."
    @echo "      Start one with: temporal server start-dev --namespace default"
    dotnet test {{integration_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --logger "console;verbosity=normal"

# Run both unit and integration tests
test: test-unit test-integration

# Run unit tests with code coverage
test-coverage: build
    dotnet test {{unit_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --collect "XPlat Code Coverage" \
        --results-directory {{artifacts_dir}}/coverage \
        --logger "console;verbosity=normal"

# Run tests matching a filter expression (e.g. just test-filter "FullyQualifiedName~Router")
test-filter filter: build
    dotnet test {{unit_tests_dir}} \
        --configuration {{configuration}} \
        --no-build \
        --filter "{{filter}}" \
        --logger "console;verbosity=normal"

# Pack NuGet packages (Release, into artifacts/packages/)
pack: clean build
    dotnet pack {{solution}} \
        --configuration {{configuration}} \
        --no-build \
        --output {{artifacts_dir}}
    @echo "Packages written to {{artifacts_dir}}/"

# Publish to NuGet.org (requires NUGET_API_KEY env var)
publish-nuget: pack
    dotnet nuget push "{{artifacts_dir}}/*.nupkg" \
        --api-key "$NUGET_API_KEY" \
        --source "https://api.nuget.org/v3/index.json" \
        --skip-duplicate

# Publish to GitHub Packages (requires NUGET_GITHUB_TOKEN env var)
publish-github: pack
    dotnet nuget push "{{artifacts_dir}}/*.nupkg" \
        --api-key "$NUGET_GITHUB_TOKEN" \
        --source "https://nuget.pkg.github.com/cecilphillip/index.json" \
        --skip-duplicate

# Alias: build
compile: build

# Alias: test-unit
verify: test-unit

# Build + unit test (no server required)
validate: build test-unit

# Full local CI pipeline: clean → build → test-unit → pack
ci: clean build test-unit pack
