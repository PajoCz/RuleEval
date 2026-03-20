[← README](../README.md)

# Publikování NuGet balíčků

RuleEval se skládá z **8 samostatných NuGet balíčků** — všechny jsou pod `src/`. Projekty v `tests/`, `benchmarks/` a `samples/` se nepublikují.

## Balíčky

| NuGet ID | Projekt |
|---|---|
| `RuleEval.Abstractions` | `src/RuleEval.Abstractions` |
| `RuleEval` | `src/RuleEval.Core` |
| `RuleEval.Caching` | `src/RuleEval.Caching` |
| `RuleEval.Diagnostics` | `src/RuleEval.Diagnostics` |
| `RuleEval.DependencyInjection` | `src/RuleEval.DependencyInjection` |
| `RuleEval.Database.Abstractions` | `src/RuleEval.Database.Abstractions` |
| `RuleEval.Database` | `src/RuleEval.Database` |
| `RuleEval.Database.DependencyInjection` | `src/RuleEval.Database.DependencyInjection` |

## Sdílená metadata — `Directory.Build.props`

Verzi, autora a licence nastavte centrálně v `Directory.Build.props` v kořenu solution. Tento soubor **není součástí repozitáře** a je nutné ho vytvořit před prvním publikováním.

```xml
<Project>
  <PropertyGroup>
    <VersionPrefix>1.0.0</VersionPrefix>
    <!-- VersionSuffix nastavte pro pre-release: alpha.1, beta.1, rc.1 -->
    <!-- <VersionSuffix>alpha.1</VersionSuffix> -->
    <Authors>Vaše Jméno</Authors>
    <Company>Vaše Firma</Company>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageProjectUrl>https://github.com/PajoCz/RuleEval</PackageProjectUrl>
    <RepositoryUrl>https://github.com/PajoCz/RuleEval</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageTags>rule-engine;rules;decision-table;dotnet</PackageTags>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageIcon>icon.png</PackageIcon>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
```

> Individuální `<Description>` a `<PackageId>` jsou nastaveny přímo v každém `.csproj`.

## Lokální pack

```bash
# Zabalit všechny publishovatelné balíčky do ./artifacts/
dotnet pack RuleEval.sln -c Release --output ./artifacts
```

Nebo jednotlivě:

```bash
dotnet pack src/RuleEval.Abstractions/RuleEval.Abstractions.csproj -c Release --output ./artifacts
dotnet pack src/RuleEval.Core/RuleEval.Core.csproj -c Release --output ./artifacts
dotnet pack src/RuleEval.Caching/RuleEval.Caching.csproj -c Release --output ./artifacts
dotnet pack src/RuleEval.Diagnostics/RuleEval.Diagnostics.csproj -c Release --output ./artifacts
dotnet pack src/RuleEval.DependencyInjection/RuleEval.DependencyInjection.csproj -c Release --output ./artifacts
dotnet pack src/RuleEval.Database.Abstractions/RuleEval.Database.Abstractions.csproj -c Release --output ./artifacts
dotnet pack src/RuleEval.Database/RuleEval.Database.csproj -c Release --output ./artifacts
dotnet pack src/RuleEval.Database.DependencyInjection/RuleEval.Database.DependencyInjection.csproj -c Release --output ./artifacts
```

## Publikování na nuget.org

### Předpoklady

1. Účet na [nuget.org](https://www.nuget.org/) a API klíč s oprávněním `Push`.
2. API klíč uložený jako environment proměnná nebo v CI secret (nikdy plaintext v repozitáři):

```bash
$env:NUGET_API_KEY = "oy2..."   # PowerShell
export NUGET_API_KEY="oy2..."   # bash
```

### Push

```bash
dotnet nuget push ./artifacts/*.nupkg `
    --api-key $env:NUGET_API_KEY `
    --source https://api.nuget.org/v3/index.json `
    --skip-duplicate
```

`--skip-duplicate` zabrání chybě, pokud stejná verze už existuje.

## Verzování

Doporučený postup:

| Fáze | `VersionPrefix` | `VersionSuffix` | Výsledná verze |
|---|---|---|---|
| Vývoj | `1.0.0` | `alpha.1` | `1.0.0-alpha.1` |
| RC | `1.0.0` | `rc.1` | `1.0.0-rc.1` |
| Stabilní release | `1.0.0` | *(prázdné)* | `1.0.0` |

Verzi lze přepsat z příkazové řádky bez úpravy souborů:

```bash
dotnet pack RuleEval.sln -c Release -p:VersionPrefix=1.2.0 --output ./artifacts
```

## GitHub Actions — automatické publikování

Ukázkový workflow pro automatické publikování při vytvoření Git tagu (`v*`):

```yaml
# .github/workflows/publish-nuget.yml
name: Publish NuGet

on:
  push:
    tags:
      - 'v*'

jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Restore
        run: dotnet restore RuleEval.sln

      - name: Build
        run: dotnet build RuleEval.sln -c Release --no-restore

      - name: Test
        run: dotnet test RuleEval.sln -c Release --no-build

      - name: Pack
        run: |
          VERSION="${GITHUB_REF_NAME#v}"
          dotnet pack RuleEval.sln -c Release --no-build \
            -p:VersionPrefix="$VERSION" \
            --output ./artifacts

      - name: Push to nuget.org
        run: |
          dotnet nuget push ./artifacts/*.nupkg \
            --api-key ${{ secrets.NUGET_API_KEY }} \
            --source https://api.nuget.org/v3/index.json \
            --skip-duplicate
```

### Nastavení GitHub secret

1. Přejděte na **Settings → Secrets and variables → Actions** v repozitáři.
2. Přidejte secret `NUGET_API_KEY` s hodnotou vašeho API klíče z nuget.org.

## Lokální NuGet feed (interní)

Pro interní distribuce lze použít Azure Artifacts nebo lokální feed:

```bash
# Publikování na Azure Artifacts
dotnet nuget push ./artifacts/*.nupkg \
    --api-key az \
    --source https://pkgs.dev.azure.com/{organization}/{feed}/v3/index.json

# Nebo do lokálního adresáře jako NuGet feed
dotnet nuget push ./artifacts/*.nupkg --source C:\LocalNuGet
```

## Doporučený postup vydání

1. Aktualizujte `VersionPrefix` (a případně `VersionSuffix`) v `Directory.Build.props`.
2. Spusťte testy: `dotnet test RuleEval.sln -c Release`.
3. Zabalte: `dotnet pack RuleEval.sln -c Release --output ./artifacts`.
4. Ověřte obsah `.nupkg` (např. přes [NuGet Package Explorer](https://github.com/NuGetPackageExplorer/NuGetPackageExplorer)).
5. Vytvořte Git tag: `git tag v1.0.0 && git push origin v1.0.0`.
6. GitHub Actions workflow automaticky zabalí a publikuje balíčky.
